using Noexs.Util;
using Avalonia.Threading;

namespace Noexs.UI.Services;

/// <summary>
/// Stand-in for javafx.concurrent.Service: runs work on the thread pool, exposes coalesced
/// message/progress updates and success/failure callbacks on the UI thread.
/// </summary>
public abstract class BackgroundService
{
    private TaskContext? current;

    /// <summary>Raised on the UI thread.</summary>
    public event Action<string>? MessageChanged;

    /// <summary>Raised on the UI thread; a negative value means "indeterminate".</summary>
    public event Action<double>? ProgressChanged;

    public double Progress { get; private set; } = -1;

    public string Message { get; private set; } = "";

    public bool IsRunning => current is { Finished: false, Token.IsCancellationRequested: false };

    public bool Cancel()
    {
        TaskContext? ctx = current;
        if (ctx == null || ctx.Finished || ctx.Token.IsCancellationRequested)
        {
            return false;
        }
        ctx.CancelSource.Cancel();
        return true;
    }

    protected TaskContext BeginRun()
    {
        Cancel();
        var ctx = new TaskContext(this);
        current = ctx;
        SetProgress(ctx, -1);
        SetMessage(ctx, "");
        return ctx;
    }

    protected bool IsCurrent(TaskContext ctx)
    {
        return ReferenceEquals(current, ctx);
    }

    internal void SetMessage(TaskContext ctx, string message)
    {
        if (!IsCurrent(ctx)) return;
        Message = message;
        MessageChanged?.Invoke(message);
    }

    internal void SetProgress(TaskContext ctx, double progress)
    {
        if (!IsCurrent(ctx)) return;
        Progress = progress;
        ProgressChanged?.Invoke(progress);
    }
}

public abstract class BackgroundService<T> : BackgroundService
{
    public Action<T>? OnSucceeded { get; set; }

    public Action<Exception>? OnFailed { get; set; }

    /// <summary>Runs on a worker thread.</summary>
    protected abstract T Call(TaskContext ctx);

    /// <summary>Cancels any running task and starts a new one.</summary>
    public void Restart()
    {
        TaskContext ctx = BeginRun();
        Task.Run(() => Call(ctx), CancellationToken.None).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ctx.Finished = true;
                if (ctx.Token.IsCancellationRequested || !IsCurrent(ctx))
                {
                    // cancelled: no callbacks, like JavaFX; release whatever the task produced
                    if (t.IsCompletedSuccessfully && t.Result is IDisposable orphan)
                    {
                        try
                        {
                            orphan.Dispose();
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "disposing cancelled result");
                        }
                    }
                    return;
                }
                try
                {
                    if (t.IsFaulted)
                    {
                        Exception e = t.Exception!.InnerExceptions.Count == 1 ? t.Exception.InnerException! : t.Exception;
                        Log.Error(e, GetType().Name + " failed");
                        OnFailed?.Invoke(e);
                    }
                    else if (t.IsCompletedSuccessfully)
                    {
                        OnSucceeded?.Invoke(t.Result);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, GetType().Name + " callback");
                    MessageDialog.ShowMessage(e);
                }
            });
        }, TaskScheduler.Default);
    }
}

public sealed class TaskContext
{
    private readonly BackgroundService owner;
    private string pendingMessage = "";
    private int messagePosted;
    private double pendingProgress;
    private int progressPosted;

    internal TaskContext(BackgroundService owner)
    {
        this.owner = owner;
    }

    internal CancellationTokenSource CancelSource { get; } = new();

    internal volatile bool Finished;

    public CancellationToken Token => CancelSource.Token;

    public bool IsCancelled => CancelSource.IsCancellationRequested;

    // Updates are coalesced so tight loops don't flood the dispatcher (JavaFX does the same).
    public void UpdateMessage(string message)
    {
        Volatile.Write(ref pendingMessage, message);
        if (Interlocked.Exchange(ref messagePosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref messagePosted, 0);
                owner.SetMessage(this, Volatile.Read(ref pendingMessage));
            });
        }
    }

    public void UpdateProgress(double workDone, double max)
    {
        double p = max <= 0 || double.IsNaN(workDone) ? -1 : Math.Clamp(workDone / max, 0, 1);
        Interlocked.Exchange(ref pendingProgress, p);
        if (Interlocked.Exchange(ref progressPosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref progressPosted, 0);
                owner.SetProgress(this, Interlocked.CompareExchange(ref pendingProgress, 0, 0));
            });
        }
    }
}

/// <summary>Fixed-rate background job (java.util.Timer.scheduleAtFixedRate).</summary>
public static class Repeating
{
    public static void Start(TimeSpan period, Action action, CancellationToken token)
    {
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }
}

/// <summary>A service whose work is supplied as a delegate (one-off background jobs).</summary>
public sealed class ActionService<T> : BackgroundService<T>
{
    public Func<TaskContext, T>? Work { get; set; }

    protected override T Call(TaskContext ctx)
    {
        return (Work ?? throw new InvalidOperationException("No work set"))(ctx);
    }
}
