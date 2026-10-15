using System.Net.Sockets;
using Noexs.Core;
using Noexs.IO.Net;
using Noexs.IO.Usb;
using Noexs.UI.Models;

namespace Noexs.UI.Services;

/// <summary>Connects with retries (a javafx ScheduledService with a maximum failure count in Java).</summary>
public sealed class DebuggerConnectionService : BackgroundService<IConnection>
{
    private string host = NetworkConstants.DEFAULT_HOST;
    private int port = NetworkConstants.DEFAULT_PORT;
    private int timeout = 1000;
    private ConnectionType type = ConnectionType.NETWORK;

    public int MaximumFailureCount { get; } = 10;

    public void SetTimeout(int timeout)
    {
        this.timeout = timeout;
    }

    protected override IConnection Call(TaskContext ctx)
    {
        for (int attempt = 0; ; attempt++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.UpdateMessage(type == ConnectionType.USB
                ? "Connecting via USB (Attempt:" + (attempt + 1) + "/" + MaximumFailureCount + ")"
                : "Connecting to:" + host + ":" + port + " (Attempt:" + (attempt + 1) + "/" + MaximumFailureCount + ")");
            try
            {
                return type == ConnectionType.USB ? UsbUtils.OpenSwitch() : Connect(ctx.Token);
            }
            catch (Exception) when (attempt < MaximumFailureCount - 1 && !ctx.IsCancelled)
            {
                // retry (the console may still be booting / the cable just got plugged in)
                Thread.Sleep(type == ConnectionType.USB ? 500 : 0);
                continue;
            }
        }
    }

    private SocketConnection Connect(CancellationToken token)
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            try
            {
                s.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("connect timed out");
            }
            s.NoDelay = true;
            return new SocketConnection(s);
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public void SetHost(string host)
    {
        this.host = host;
    }

    public void SetPort(int port)
    {
        this.port = port;
    }

    public void SetType(ConnectionType type)
    {
        this.type = type;
    }
}
