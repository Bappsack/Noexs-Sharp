using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noexs.Core;
using Noexs.UI.Controls;
using Noexs.UI.Models;
using Noexs.Util;

namespace Noexs.UI.Views;

public partial class WatchlistTab : UserControl, IController
{
    private MainWindow mc = null!;

    private readonly SemaphoreSlim semaphore = new(1, 1);
    private readonly ObservableCollection<WatchlistModel> items = new();

    public WatchlistTab()
    {
        InitializeComponent();
        watchlistTable.ItemsSource = items;
        GridCells.CommitOnEnter(watchlistTable);
        watchlistTable.SelectionChanged += (_, _) => removeButton.IsEnabled = watchlistTable.SelectedItem != null;
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
        mc.Schedule(TimeSpan.FromMilliseconds(100), WatchlistTimerTask);
    }

    public void OnConnect()
    {
        IsEnabled = true;
    }

    public void OnDisconnect()
    {
        //IsEnabled = false;
    }

    public void AddAddr(long addr)
    {
        var model = new WatchlistModel();
        SetAddr(model, addr);
        model.Update = true;
        Update(model);
    }

    /// <summary>Adds an address expression such as a pointer path ("[[main+1A2B]+10]+08").</summary>
    public void AddExpression(string expression)
    {
        var model = new WatchlistModel
        {
            Addr = expression,
            Update = true,
        };
        Update(model);
    }

    private void Update(WatchlistModel model)
    {
        semaphore.Wait();
        try
        {
            items.Add(model);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void OnAddAction(object? sender, RoutedEventArgs e)
    {
        Update(new WatchlistModel());
    }

    private void OnRemoveAction(object? sender, RoutedEventArgs e)
    {
        if (watchlistTable.SelectedItem is not WatchlistModel model) return;
        semaphore.Wait();
        try
        {
            items.Remove(model);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void ClearList(object? sender, RoutedEventArgs e)
    {
        semaphore.Wait();
        try
        {
            items.Clear();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        string? f = await mc.BrowseFile(true, "Save As...", "Watchlist File", "*.json");
        if (f == null)
        {
            return;
        }
        var list = items.Select(item => new SerializedWatchlistItem
        {
            Update = item.Update,
            Locked = item.Locked,
            Addr = item.Addr,
            Desc = item.Desc,
            Type = item.Type,
            Value = item.Value,
        }).ToList();
        string json = JsonSerializer.Serialize(list, NoexsJsonContext.Default.ListSerializedWatchlistItem);
        try
        {
            await File.WriteAllTextAsync(f, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex);
            MessageDialog.ShowMessage(ex);
        }
    }

    private async void OnLoad(object? sender, RoutedEventArgs e)
    {
        string? f = await mc.BrowseFile(false, "Open...", "Watchlist File", "*.json");
        if (f == null)
        {
            return;
        }

        List<SerializedWatchlistItem>? list;
        try
        {
            list = JsonSerializer.Deserialize(await File.ReadAllTextAsync(f),
                NoexsJsonContext.Default.ListSerializedWatchlistItem);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex);
            MessageDialog.ShowMessage(ex);
            return;
        }

        semaphore.Wait();
        try
        {
            items.Clear();
            foreach (SerializedWatchlistItem? item in list ?? [])
            {
                if (item == null)
                {
                    continue;
                }
                var model = new WatchlistModel
                {
                    Update = item.Update,
                    Locked = item.Locked,
                    Addr = item.Addr ?? HexUtils.FormatAddress(0),
                    Desc = item.Desc ?? "-",
                    Type = item.Type ?? DataType.Int,
                    Value = item.Value,
                };
                items.Add(model);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Runs every 100ms on a background thread.</summary>
    private void WatchlistTimerTask()
    {
        Debugger debugger = mc.GetDebugger();
        if (!debugger.Connected)
        {
            return;
        }
        semaphore.Wait();
        try
        {
            foreach (WatchlistModel m in items)
            {
                if (!debugger.Connected)
                {
                    break;
                }
                // per entry: an invalid address expression (often one still being typed) must not
                // stop the other entries from updating, as it did in the Java version
                try
                {
                    long addr = GetAddr(m);
                    if (addr == 0)
                    {
                        continue;
                    }
                    if (m.Update)
                    {
                        long value = debugger.Peek(m.Type, addr);
                        if (m.Value != value)
                        {
                            Dispatcher.UIThread.Post(() => m.Value = value);
                        }
                    }
                    else if (m.Locked)
                    {
                        debugger.Poke(m.Type, addr, m.Value);
                    }
                }
                catch (Exception)
                {
                    // ignored, like the Java timer task
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static void SetAddr(WatchlistModel m, long addr)
    {
        m.Addr = HexUtils.FormatAddress(addr);
    }

    private long GetAddr(WatchlistModel m)
    {
        return mc.Tools().GetEvaluator().Eval(m.Addr);
    }
}
