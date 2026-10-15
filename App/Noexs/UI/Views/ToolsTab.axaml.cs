using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noexs.Core;
using Noexs.Misc;
using Noexs.UI.Models;
using Noexs.Util;

namespace Noexs.UI.Views;

public partial class ToolsTab : UserControl, IController
{
    private MainWindow mc = null!;

    private readonly ObservableCollection<MemoryInfoTableModel> memoryInfoList = new();
    private readonly ObservableCollection<PidEntry> pids = new();

    /// <summary>A process in the PID list, shown as "Game name (pid)".</summary>
    private sealed record PidEntry(long Pid, long TitleId)
    {
        public override string ToString()
        {
            return TitleDb.Describe(TitleId, Pid);
        }
    }

    private readonly ConcurrentDictionary<string, long> vars = new();

    private readonly ExpressionEvaluator evaluator;

    private sealed class VariableProvider(ConcurrentDictionary<string, long> vars) : ExpressionEvaluator.IVariableProvider
    {
        public long Get(string name)
        {
            return vars[name];
        }

        public bool ContainsVar(string value)
        {
            return vars.ContainsKey(value);
        }
    }

    public ToolsTab()
    {
        InitializeComponent();

        evaluator = new ExpressionEvaluator(new VariableProvider(vars), addr =>
        {
            Debugger debugger = mc.GetDebugger();
            if (!debugger.Connected)
            {
                return 0;
            }
            return debugger.Peek64(addr);
        });

        memInfoTable.ItemsSource = memoryInfoList;
        pidList.ItemsSource = pids;

        pidList.SelectionChanged += (_, _) =>
        {
            if (pidList.SelectedItem is PidEntry entry)
            {
                DisplayTitleId(entry.TitleId);
            }
        };
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
        memInfoTable.ContextMenu = MemoryInfoContextMenu.Create(() => mc, memInfoTable);
    }

    public void OnConnect()
    {
        IsEnabled = true;
    }

    public void OnDisconnect()
    {
        IsEnabled = false;
    }

    private void SetPidsList(object? sender, RoutedEventArgs e)
    {
        mc.Guard(SetPidsList);
    }

    private void SetPidsList()
    {
        pids.Clear();
        Debugger debugger = mc.GetDebugger();
        if (debugger.Connected)
        {
            long current = 0;
            try
            {
                current = debugger.GetCurrentPid(); // the running game, if any
            }
            catch (ConnectionException) when (debugger.Connected)
            {
            }
            PidEntry? game = null;
            foreach (long pid in debugger.GetPids())
            {
                long tid = 0;
                try
                {
                    tid = debugger.GetTitleId(pid);
                }
                catch (ConnectionException) when (debugger.Connected)
                {
                    // not every process has a program id
                }
                // games: ask the console for the exact name once (remembered for later and offline dumps)
                if (TitleDb.IsApplicationId(tid) && !TitleDb.HasConsoleName(tid))
                {
                    try
                    {
                        if (debugger.GetTitleName(tid) is { } consoleName)
                        {
                            TitleDb.RememberConsoleName(tid, consoleName);
                        }
                    }
                    catch (ConnectionException e) when (debugger.Connected)
                    {
                        Log.Info("title name for " + HexUtils.FormatTitleId(tid) + ": " + e.Message);
                    }
                }
                var entry = new PidEntry(pid, tid);
                pids.Add(entry);
                if (pid == current)
                {
                    game = entry;
                }
            }
            if (game != null)
            {
                pidList.SelectedItem = game;
                pidList.ScrollIntoView(game);
            }
        }
        else
        {
            MessageDialog.ShowMessage("You are not currently connected.", AlertType.WARNING);
        }
    }

    private void DetachProcess(object? sender, RoutedEventArgs e)
    {
        mc.Guard(DetachProcess);
    }

    private void DetachProcess()
    {
        Debugger conn = mc.GetDebugger();
        if (conn.Connected && conn.Attached)
        {
            Result result = conn.Detach();
            if (result.Succeeded)
            {
                mc.SetTitle(null);
                mc.SetStatus("Detached from process");
                memoryInfoList.Clear();
            }
            else
            {
                MessageDialog.ShowMessage("Detach failed.", result, AlertType.ERROR);
                mc.SetStatus("Error");
            }
        }
        else
        {
            MessageDialog.ShowMessage("Not attached.", AlertType.WARNING);
        }
    }

    private void AttachProcess(object? sender, RoutedEventArgs e)
    {
        mc.Guard(AttachProcess);
    }

    private void AttachProcess()
    {
        Debugger conn = mc.GetDebugger();
        if (conn.Connected)
        {
            long selectedPid = pidList.SelectedItem is PidEntry selected ? selected.Pid : 0;

            if (selectedPid != 0)
            {
                Result attached = conn.Attach(selectedPid);
                if (attached.Failed)
                {
                    MessageDialog.ShowMessage("Attach failed.", attached, AlertType.ERROR);
                }
                else
                {
                    mc.SetStatus("Attached to process");
                    long tid = conn.GetTitleId(selectedPid);
                    string? name = TitleDb.Lookup(tid);
                    mc.SetTitle("Attached to: " + (name != null ? name + " " : "") + "0x" + HexUtils.FormatTitleId(tid));
                    mc.Memory().PopulateMemory();
                }
            }
            else
            {
                MessageDialog.ShowMessage("Invalid pid selected.", AlertType.ERROR);
            }
        }
        else
        {
            MessageDialog.ShowMessage("You are not currently connected.", AlertType.WARNING);
        }
    }

    public void DisplayTitleId(long titleId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            string? name = TitleDb.Lookup(titleId);
            toolsTitleId.Text = "Title Id:" + (titleId == -1 ? "N/A" : HexUtils.FormatTitleId(titleId)) +
                                (name != null ? "\n" + name : "");
        });
    }

    private async void OnUpdateTitles(object? sender, RoutedEventArgs e)
    {
        updateTitlesButton.IsEnabled = false;
        mc.SetStatus("Downloading the title list from switchbrew.org...");
        try
        {
            int count = await TitleDb.UpdateFromSwitchbrewAsync();
            mc.SetStatus($"Title list updated: {count} titles");
            // re-add the entries so the list shows the new names
            var current = pids.ToList();
            object? selected = pidList.SelectedItem;
            pids.Clear();
            foreach (PidEntry p in current)
            {
                pids.Add(p);
            }
            pidList.SelectedItem = selected;
            if (selected is PidEntry s)
            {
                DisplayTitleId(s.TitleId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "title list update");
            mc.SetStatus("Title list update failed");
            MessageDialog.ShowMessage("Could not update the title list from switchbrew.org:\n" + ex.Message, AlertType.WARNING);
        }
        finally
        {
            updateTitlesButton.IsEnabled = true;
        }
    }

    public void UpdateMemoryInfo(MemoryInfo[] info)
    {
        memoryInfoList.Clear();
        int moduleCount = 0;

        foreach (MemoryInfo m in info)
        {
            if (m.Type == MemoryType.CODE_STATIC && m.Perm == 0b101)
            {
                moduleCount++;
            }
        }
        int mod = 0;
        bool heap = false;
        var rows = new List<MemoryInfoTableModel>();
        foreach (MemoryInfo m in info)
        {
            string name = "-";
            if (m.Type == MemoryType.HEAP && !heap)
            {
                heap = true;
                name = "heap";
            }
            if (m.Type == MemoryType.CODE_STATIC && m.Perm == 0b101)
            {
                if (mod == 0 && moduleCount == 1)
                {
                    name = "main";
                }
                if (moduleCount > 1)
                {
                    name = mod switch
                    {
                        0 => "rtld",
                        1 => "main",
                        2 => "sdk",
                        _ => "subsdk" + (mod - 2),
                    };
                }
                mod++;
            }
            if (name != "-")
            {
                vars[name] = m.Address;
            }
            if (m.Type != MemoryType.UNMAPPED)
            {
                rows.Add(new MemoryInfoTableModel(name, m));
            }
        }
        // the Java table was a SortedList ordered by address
        rows.Sort((a, b) => a.Addr.CompareTo(b.Addr));
        foreach (var row in rows)
        {
            memoryInfoList.Add(row);
        }
    }

    public ExpressionEvaluator GetEvaluator()
    {
        return evaluator;
    }

    private void OnParse(object? sender, RoutedEventArgs e)
    {
        try
        {
            string str = expression.Text ?? "";
            expressionResult.Text = HexUtils.FormatAddress(evaluator.Eval(str));
        }
        catch (Exception ex)
        {
            MessageDialog.ShowMessage(ex);
        }
    }
}

/// <summary>Context menu for the memory-info table (menus/MemoryInfoContextMenu.java).</summary>
public static class MemoryInfoContextMenu
{
    public static ContextMenu Create(Func<MainWindow> mc, DataGrid memInfoTable)
    {
        MenuItem Item(string header, Action<MemoryInfoTableModel> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                if (memInfoTable.SelectedItem is MemoryInfoTableModel model)
                {
                    mc().Guard(() => action(model));
                }
            };
            return item;
        }

        var cm = new ContextMenu();
        cm.Items.Add(Item("Search (Start & End)", model =>
        {
            mc().Search().SetSearchRange(model.Addr, model.End);
            mc().SetTab(MainWindow.Tab.SEARCH);
        }));
        cm.Items.Add(Item("Search(Start)", model => mc().Search().SetStart(model.Addr)));
        cm.Items.Add(Item("Search (End)", model => mc().Search().SetEnd(model.End)));
        cm.Items.Add(Item("Pointer Search (Main Start & End)", model =>
        {
            mc().Pointer().SetMainRange(model.Addr, model.End);
            mc().SetTab(MainWindow.Tab.POINTER_SEARCH);
        }));
        cm.Items.Add(Item("Pointer Search (Heap Start & End)", model =>
        {
            mc().Pointer().SetHeapRange(model.Addr, model.End);
            mc().SetTab(MainWindow.Tab.POINTER_SEARCH);
        }));
        cm.Items.Add(Item("Memory Viewer", model =>
        {
            mc().Memory().SetViewAddress(model.Addr);
            mc().SetTab(MainWindow.Tab.MEMORY_VIEWER);
        }));
        cm.Items.Add(Item("Disassembler", model =>
        {
            mc().Disassembly().SetDisassembleAddress(model.Addr);
            mc().SetTab(MainWindow.Tab.DISASSEMBLER);
        }));
        return cm;
    }
}
