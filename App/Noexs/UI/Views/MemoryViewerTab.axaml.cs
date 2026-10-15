using System.Buffers.Binary;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noexs.Core;
using Noexs.UI.Models;
using Noexs.Util;

namespace Noexs.UI.Views;

public partial class MemoryViewerTab : UserControl, IController
{
    private const int ROW_COUNT = 100;

    private MainWindow mainController = null!;

    private long prevUpdateAddr;

    private readonly ObservableCollection<MemoryViewerTableModel> memoryList = new();
    private long lastAddress;
    private readonly DispatcherTimer refreshTimer;

    private readonly PatternTokenizer tokenizer = new();
    private readonly PatternCompiler compiler = new();

    public MemoryViewerTab()
    {
        InitializeComponent();

        //setup the memview table
        for (int i = 0; i < ROW_COUNT; i++)
        {
            memoryList.Add(new MemoryViewerTableModel());
        }
        memViewTable.ItemsSource = memoryList;

        // the Java table used cell selection; the focused cell is the current column of the selected row
        memViewTable.CurrentCellChanged += (_, _) =>
        {
            long addr = GetSelectedAddress();
            memViewAddrBox.Value = addr;
            if (addr != 0 && pokeType.SelectedItem is DataType t && mainController.GetDebugger().Connected)
            {
                try
                {
                    pokeValue.Value = mainController.GetDebugger().Peek(t, addr);
                }
                catch (Exception e)
                {
                    // unreadable address: leave the poke value as it is
                    Log.Error(e, "peek " + addr.ToString("X"));
                }
            }
        };

        pokeType.ItemsSource = DataType.Values;
        pokeType.SelectedItem = DataType.Int;

        pokeType.SelectionChanged += (_, _) =>
        {
            if (pokeType.SelectedItem is DataType t)
            {
                pokeValue.SetSize(t.Size * 2);
            }
        };

        endianCheckbox.IsChecked = Settings.ShouldSwapEndian();
        endianCheckbox.IsCheckedChanged += (_, _) => TryDump(lastAddress, quiet: true);

        // runs on the UI thread, like the Java timer task did via runAndWait
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (refreshCheckbox.IsChecked == true && mainController.GetDebugger().Connected
                && mainController.GetCurrentTab() == MainWindow.Tab.MEMORY_VIEWER)
            {
                TryDump(lastAddress, quiet: true);
            }
        });
    }

    private long GetSelectedAddress()
    {
        long addr = lastAddress;
        int row = memViewTable.SelectedIndex;
        DataGridColumn? column = memViewTable.CurrentColumn;
        if (addr == 0 || row < 0 || column == null)
        {
            return 0;
        }
        int col = column.DisplayIndex;

        addr += row * 0x10;
        //is address selected
        if (col == 0)
        {
            return addr;
        }

        //is ascii selected
        if (col > 4)
        {
            return addr + (col - 5);
        }
        col--;
        return addr + col * 4;
    }

    public void Stop()
    {
        refreshTimer.Stop();
        Settings.SetSwapEndian(endianCheckbox.IsChecked == true);
    }

    public void DumpToMemoryViewer()
    {
        DumpToMemoryViewer(memViewAddrBox.Value);
    }

    /// <summary>
    /// Reads and shows memory, reporting failures (unmapped address, lost connection) in the status
    /// bar instead of throwing. Auto-refresh failures are silent so they don't repeat every 500ms.
    /// </summary>
    private void TryDump(long updateAddr, bool quiet = false)
    {
        if (updateAddr == 0 || !mainController.GetDebugger().Connected)
        {
            return;
        }
        try
        {
            DumpToMemoryViewer(updateAddr);
        }
        catch (Exception e)
        {
            Log.Error(e, "memory viewer " + updateAddr.ToString("X"));
            if (!quiet)
            {
                mainController.SetStatus("Unable to read memory at " + HexUtils.FormatAddress(updateAddr) + ": " + e.Message);
            }
        }
    }

    public void DumpToMemoryViewer(long updateAddr)
    {
        if (updateAddr == 0)
        {
            return;
        }

        bool swap = endianCheckbox.IsChecked == true;

        long @base = updateAddr & ~0xFL;
        Debugger conn = mainController.GetDebugger();
        int size = ROW_COUNT * 16;
        byte[] data = conn.Readmem(@base, size, new byte[size]);
        lastAddress = @base;
        for (int i = 0; i < ROW_COUNT; i++)
        {
            long addr = @base + i * 0x10;
            int off = i * 16;
            MemoryViewerTableModel m = memoryList[i];
            m.Set(addr, ReadInt(data, off, swap), ReadInt(data, off + 4, swap),
                ReadInt(data, off + 8, swap), ReadInt(data, off + 12, swap));
        }
        mainController.RunAndWait(() =>
        {
            if (updateAddr == prevUpdateAddr)
            {
                return;
            }
            prevUpdateAddr = updateAddr;
            long l = updateAddr & ~3L;
            int columnIndex = (int)((l & 0xF) / 4) + 1;
            memViewTable.SelectedIndex = 0;
            memViewTable.CurrentColumn = memViewTable.Columns[columnIndex];
        });
    }

    // ByteBuffer defaults to big-endian; "Switch Endianess" reads little-endian.
    private static int ReadInt(byte[] data, int off, bool littleEndian)
    {
        var span = data.AsSpan(off, 4);
        return littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(span) : BinaryPrimitives.ReadInt32BigEndian(span);
    }

    private void Navigate(Action move)
    {
        if (!mainController.RequireConnection())
        {
            return;
        }
        move();
        TryDump(memViewAddrBox.Value);
    }

    private void UpdateMemoryViewer(object? sender, RoutedEventArgs e)
    {
        Navigate(() => { });
    }

    private void MemViewerUpBig(object? sender, RoutedEventArgs e)
    {
        Navigate(() => memViewAddrBox.Decrement(ROW_COUNT * 0x10));
    }

    private void MemViewerUp(object? sender, RoutedEventArgs e)
    {
        Navigate(() => memViewAddrBox.Decrement(0x10));
    }

    public void SetViewAddress(long addr)
    {
        memViewAddrBox.Value = addr;
        TryDump(addr);
    }

    private void MemViewerDownBig(object? sender, RoutedEventArgs e)
    {
        Navigate(() => memViewAddrBox.Increment(ROW_COUNT * 0x10));
    }

    private void MemViewerDown(object? sender, RoutedEventArgs e)
    {
        Navigate(() => memViewAddrBox.Increment(0x10));
    }

    public void SetMainController(MainWindow mc)
    {
        mainController = mc;
        refreshTimer.Start();
    }

    public void OnConnect()
    {
        //IsEnabled = true;
    }

    public void OnDisconnect()
    {
        //IsEnabled = false;
    }

    public void PopulateMemory()
    {
        Debugger conn = mainController.GetDebugger();
        if (!conn.Connected)
        {
            return;
        }
        //TODO not do this here.
        mainController.Tools().UpdateMemoryInfo(conn.Query(0, 10000));
    }

    private void OnPokeAction(object? sender, RoutedEventArgs e)
    {
        if (pokeType.SelectedItem is not DataType t || !mainController.RequireConnection()) return;
        mainController.Guard(() =>
        {
            Debugger debugger = mainController.GetDebugger();
            debugger.Poke(t, memViewAddrBox.Value, pokeValue.Value);
            TryDump(lastAddress);
        });
    }

    private void OnPatternSearch(object? sender, RoutedEventArgs e)
    {
        long address = memViewAddrBox.Value;
        if (address == 0)
        {
            MessageDialog.ShowMessage("Please enter a start address first.", AlertType.WARNING);
            return;
        }
        string str = (patternTextField.Text ?? "").Trim();
        if (str.Length == 0)
        {
            MessageDialog.ShowMessage("Please enter a pattern.", AlertType.WARNING);
            return;
        }
        if (!mainController.RequireConnection())
        {
            return;
        }

        IPatternMatcher matcher;
        try
        {
            PatternCompiler.PatternElement?[] elements = tokenizer.Eval(str);
            matcher = compiler.Compile(elements);
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or OverflowException)
        {
            MessageDialog.ShowMessage("Invalid pattern: " + ex.Message, AlertType.WARNING);
            return;
        }

        //TODO do this in a service, not here
        mainController.Guard(() =>
        {
            Debugger debugger = mainController.GetDebugger();
            MemoryInfo info = debugger.Query(address);
            long remaining = info.NextAddress - address;
            if (!info.IsReadable || remaining <= 0)
            {
                mainController.SetStatus("Pattern search: address is not readable");
                return;
            }
            int size = (int)Math.Min(remaining, int.MaxValue);
            byte[] bytes = debugger.Readmem(address, size, new byte[size]);
            int idx = matcher.Match(bytes);
            if (idx != -1)
            {
                memViewAddrBox.Value = address + idx;
                TryDump(memViewAddrBox.Value);
            }
            else
            {
                mainController.SetStatus("Pattern not found");
            }
        });
    }
}
