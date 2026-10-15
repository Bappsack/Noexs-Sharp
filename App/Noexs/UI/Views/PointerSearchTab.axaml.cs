using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noexs.Core;
using Noexs.Dump;
using Noexs.Pointers;
using Noexs.UI.Controls;
using Noexs.UI.Models;
using Noexs.UI.Services;
using Noexs.Util;

namespace Noexs.UI.Views;

/// <summary>
/// Pointer search tab, following the workflow of PointerSearcher by Takumi4685:
/// read the 1st dump, search backwards from the target to main, narrow down with more dumps.
/// </summary>
public partial class PointerSearchTab : UserControl, IController
{
    private MainWindow mc = null!;

    private readonly ObservableCollection<PointerDumpRow> rows = new();

    private readonly ActionService<PointerInfo> readService = new();
    private readonly ActionService<(List<PointerPath> Paths, bool LimitReached)> searchService = new();
    private readonly ActionService<List<PointerPath>> narrowService = new();
    private BackgroundService? running;

    private PointerInfo? info;
    private List<PointerPath> result = new();

    public PointerSearchTab()
    {
        InitializeComponent();

        rows.Add(new PointerDumpRow());
        dumpRows.ItemsSource = rows;
        GridCells.CommitOnEnter(dumpRows);
        dumpRows.SelectionChanged += (_, _) => removeRowButton.IsEnabled = dumpRows.SelectedIndex > 0;
        removeRowButton.IsEnabled = false;
        // "…" buttons inside the File cells
        dumpRows.AddHandler(Button.ClickEvent, OnGridButtonClick);

        depthSpinner.IntValue = Math.Clamp(Settings.GetPointerDepth(), 1, 20);
        offsetRangeSpinner.Value = Settings.GetPointerOffset();
        offsetNumSpinner.IntValue = Math.Clamp(Settings.GetPointerOffsetNum(), 1, 64);

        resultList.SelectionChanged += (_, _) =>
            resultText.Text = resultList.SelectedItem is PointerPath p ? p.ToString() : "";
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
        foreach (BackgroundService s in new BackgroundService[] { readService, searchService, narrowService })
        {
            s.MessageChanged += msg => mc.SetStatus(msg);
        }
    }

    public void OnConnect()
    {
    }

    public void OnDisconnect()
    {
    }

    public void Stop()
    {
        running?.Cancel();
        Settings.SetPointerDepth(depthSpinner.IntValue);
        Settings.SetPointerOffset(offsetRangeSpinner.Value);
        Settings.SetPointerOffsetNum(offsetNumSpinner.IntValue);
    }

    // ---- integration with other tabs ----

    private PointerDumpRow FirstRow => rows[0];

    public void SetMainRange(long start, long end)
    {
        FirstRow.MainStart = start.ToString("X");
        FirstRow.MainEnd = end.ToString("X");
    }

    public void SetHeapRange(long start, long end)
    {
        FirstRow.HeapStart = start.ToString("X");
        FirstRow.HeapEnd = end.ToString("X");
    }

    public void SetTarget(long address)
    {
        FirstRow.Target = address.ToString("X");
    }

    // ---- dump rows ----

    private async void OnGridButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control source
            || source.FindAncestorOfType<Button>(includeSelf: true) is not { DataContext: PointerDumpRow row } button
            || !button.Classes.Contains("browse"))
        {
            return;
        }
        e.Handled = true;
        string? f = await mc.BrowseFile(false, "select Noexs dump file", "NoexsDumpFile", "*.dmp");
        if (f == null)
        {
            return;
        }
        row.File = f;
        AutoFill(row, f);
    }

    /// <summary>Main Start/End = first/last address of main, Heap Start/End = first/last heap address.</summary>
    private void AutoFill(PointerDumpRow row, string dumpPath)
    {
        try
        {
            List<MemoryInfo> infos;
            using (var dump = new MemoryDump(dumpPath, readOnly: true))
            {
                infos = dump.Infos.ToList();
            }
            if (infos.Count == 0 && mc.GetDebugger().Connected)
            {
                infos = mc.GetDebugger().Query(0, 10000).ToList();
            }
            if (Apply(row, MemoryRegions.Detect(infos)))
            {
                mc.SetStatus("Main/heap ranges filled from " + Path.GetFileName(dumpPath));
            }
            else
            {
                mc.SetStatus("The dump has no memory map; enter main/heap ranges manually");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "reading dump " + dumpPath);
            MessageDialog.ShowMessage("Not a valid Noexes dump:\n" + ex.Message, AlertType.ERROR);
        }
    }

    private static bool Apply(PointerDumpRow row, MemoryRegions.Ranges r)
    {
        if (r.MainStart is { } ms) row.MainStart = ms.ToString("X");
        if (r.MainEnd is { } me) row.MainEnd = me.ToString("X");
        if (r.HeapStart is { } hs) row.HeapStart = hs.ToString("X");
        if (r.HeapEnd is { } he) row.HeapEnd = he.ToString("X");
        return r.MainStart != null || r.HeapStart != null;
    }

    private void OnFromSearch(object? sender, RoutedEventArgs e)
    {
        DumpPicker.Show(fromSearchButton, entry =>
        {
            PointerDumpRow row = dumpRows.SelectedItem as PointerDumpRow ?? FirstRow;
            row.File = entry.Path;
            AutoFill(row, entry.Path);
            if (entry.SingleResult is { } target && string.IsNullOrWhiteSpace(row.Target))
            {
                row.Target = target.ToString("X"); // the search narrowed it down to one address
            }
            if (!entry.Full)
            {
                MessageDialog.ShowMessage(
                    "This dump comes from a refine search, so it only contains the memory around the search results.\n" +
                    "Pointer search needs main and the heap: pick the dump of the first (full) search instead.",
                    AlertType.WARNING);
            }
        });
    }

    private void OnAddRow(object? sender, RoutedEventArgs e)
    {
        rows.Add(new PointerDumpRow { Target = "0" });
    }

    private void OnRemoveRow(object? sender, RoutedEventArgs e)
    {
        if (dumpRows.SelectedIndex > 0)
        {
            rows.RemoveAt(dumpRows.SelectedIndex);
        }
    }

    private void OnFillFromProcess(object? sender, RoutedEventArgs e)
    {
        if (!mc.RequireConnection())
        {
            return;
        }
        PointerDumpRow row = dumpRows.SelectedItem as PointerDumpRow ?? FirstRow;
        mc.Guard(() =>
        {
            if (!Apply(row, MemoryRegions.Detect(mc.GetDebugger().Query(0, 10000))))
            {
                MessageDialog.ShowMessage("Main/heap could not be found. Is a process attached?", AlertType.WARNING);
            }
        });
    }

    // ---- validation (CreateDumpDataReader) ----

    private sealed record RowValues(string File, long MainStart, long MainEnd, long HeapStart, long HeapEnd, long Target);

    private static RowValues? Validate(PointerDumpRow row, int rowNumber, bool allowUnknownTarget, bool needFile)
    {
        var errors = new List<string>();
        if (needFile && (string.IsNullOrWhiteSpace(row.File) || !File.Exists(row.File)))
        {
            errors.Add("Dump File");
        }
        long Parse(string text, string name)
        {
            if (HexUtils.TryParseHex(text, out long v))
            {
                return v;
            }
            errors.Add(name);
            return -1;
        }
        long mainStart = Parse(row.MainStart, "Main Start");
        long mainEnd = Parse(row.MainEnd, "Main End");
        long heapStart = Parse(row.HeapStart, "Heap Start");
        long heapEnd = Parse(row.HeapEnd, "Heap End");
        long target = Parse(row.Target, "Target Address");
        if (errors.Count == 0)
        {
            if (mainEnd <= mainStart)
            {
                errors.Add("Main End must be greater than Main Start");
            }
            if (heapEnd <= heapStart)
            {
                errors.Add("Heap End must be greater than Heap Start");
            }
            if (!(allowUnknownTarget && target == 0) && (target < heapStart || heapEnd <= target))
            {
                // if not unknown, the target has to be located in the heap
                errors.Add("Target Address must be inside the heap" + (allowUnknownTarget ? " (or 0)" : ""));
            }
        }
        if (errors.Count > 0)
        {
            MessageDialog.ShowMessage($"Invalid input in row {rowNumber}:\n  " + string.Join("\n  ", errors), AlertType.ERROR);
            return null;
        }
        return new RowValues(row.File, mainStart, mainEnd, heapStart, heapEnd, target);
    }

    private static DumpDataReader Open(RowValues v)
    {
        return new DumpDataReader(v.File, v.MainStart, v.MainEnd, v.HeapStart, v.HeapEnd);
    }

    // ---- actions ----

    private void SetBusy(BackgroundService? service)
    {
        running = service;
        bool busy = service != null;
        readButton.IsEnabled = !busy;
        searchButton.IsEnabled = !busy && info != null;
        narrowButton.IsEnabled = !busy && info != null && result.Count > 0;
        cancelButton.IsEnabled = busy;
        dumpRows.IsEnabled = !busy;
    }

    private void Start<T>(ActionService<T> service)
    {
        SetBusy(service);
        mc.BindProgress(service);
        service.Restart();
    }

    private void OnRead(object? sender, RoutedEventArgs e)
    {
        RowValues? v = Validate(FirstRow, 1, allowUnknownTarget: false, needFile: true);
        if (v == null)
        {
            return;
        }
        info = null;
        readService.Work = ctx =>
        {
            ctx.UpdateMessage("Reading 1st dump data...");
            using DumpDataReader reader = Open(v);
            return reader.Read(ctx.Token, p => ctx.UpdateProgress(p, 1));
        };
        readService.OnSucceeded = pi =>
        {
            info = pi;
            SetBusy(null);
            infoLabel.Text = $"1st dump read: {pi.PointedList.Count} pointed heap addresses. Set the options and Reset and Search.";
            mc.SetStatus("Read finished");
        };
        readService.OnFailed = ex =>
        {
            SetBusy(null);
            mc.SetStatus("Read Failed");
            MessageDialog.ShowMessage("Read Failed\n" + ex.Message, AlertType.ERROR);
        };
        Start(readService);
    }

    private void OnSearch(object? sender, RoutedEventArgs e)
    {
        PointerInfo? pi = info;
        if (pi == null)
        {
            return;
        }
        RowValues? v = Validate(FirstRow, 1, allowUnknownTarget: false, needFile: false);
        if (v == null)
        {
            return;
        }
        int maxDepth = depthSpinner.IntValue;
        int maxOffsetNum = offsetNumSpinner.IntValue;
        long maxOffsetAddress = offsetRangeSpinner.Value;
        if (maxOffsetNum <= 0)
        {
            MessageDialog.ShowMessage("Offset Num must be greater than 0", AlertType.ERROR);
            return;
        }
        if (maxOffsetAddress < 0)
        {
            MessageDialog.ShowMessage("Offset Range must be greater or equal to 0", AlertType.ERROR);
            return;
        }

        var address = new Address(RegionType.HEAP, v.Target - v.HeapStart);
        result = new List<PointerPath>();
        ShowResults();
        searchService.Work = ctx =>
        {
            ctx.UpdateMessage("Searching pointers...");
            double total = 0;
            var found = new List<PointerPath>();
            var find = new FindPath(maxOffsetNum, maxOffsetAddress);
            find.Search(ctx.Token, add =>
            {
                total = Math.Min(100, total + add);
                ctx.UpdateProgress(total, 100);
            }, 100.0, pi, maxDepth, new List<PathElement>(), address, found);
            return (found, find.LimitReached);
        };
        searchService.OnSucceeded = r =>
        {
            result = r.Paths;
            SetBusy(null);
            ShowResults();
            mc.SetStatus("Search finished: " + result.Count + " result(s)");
            if (r.LimitReached)
            {
                MessageDialog.ShowMessage($"Stopped after {FindPath.MaxResults} results. Lower Max Depth / Offset Num or narrow the results down.",
                    AlertType.WARNING);
            }
        };
        searchService.OnFailed = ex =>
        {
            SetBusy(null);
            mc.SetStatus("Search Failed");
            MessageDialog.ShowMessage("Search Failed\n" + ex.Message, AlertType.ERROR);
        };
        Start(searchService);
    }

    private void OnNarrowDown(object? sender, RoutedEventArgs e)
    {
        var values = new List<RowValues>();
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i].IsBlank)
            {
                continue;
            }
            RowValues? v = Validate(rows[i], i + 1, allowUnknownTarget: true, needFile: true);
            if (v == null)
            {
                return;
            }
            values.Add(v);
        }
        if (values.Count == 0)
        {
            MessageDialog.ShowMessage("Add a 2nd dump row (Add Dump Row) to narrow down", AlertType.WARNING);
            return;
        }

        List<PointerPath> current = result;
        narrowService.Work = ctx =>
        {
            ctx.UpdateMessage("Narrowing down results...");
            var readers = new List<(DumpDataReader, long)>();
            try
            {
                foreach (RowValues v in values)
                {
                    readers.Add((Open(v), v.Target));
                }
                return FindPath.NarrowDown(ctx.Token, p => ctx.UpdateProgress(p, 1), current, readers);
            }
            finally
            {
                foreach ((DumpDataReader r, _) in readers)
                {
                    r.Dispose();
                }
            }
        };
        narrowService.OnSucceeded = r =>
        {
            result = r;
            SetBusy(null);
            ShowResults();
            mc.SetStatus("Narrowed down to " + result.Count + " result(s)");
        };
        narrowService.OnFailed = ex =>
        {
            SetBusy(null);
            mc.SetStatus("Narrow down failed");
            MessageDialog.ShowMessage(ex.Message, AlertType.ERROR);
        };
        Start(narrowService);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (running != null && running.Cancel())
        {
            mc.UnbindProgress();
            mc.SetProgress(0);
            mc.SetStatus("Canceled");
        }
        SetBusy(null);
    }

    private void ShowResults()
    {
        resultList.ItemsSource = null;
        resultList.ItemsSource = result;
        resultText.Text = "";
        infoLabel.Text = result.Count == 0
            ? (info == null ? "Read the 1st dump to start." : "not found")
            : $"{result.Count} result(s)" + (rows.Count > 1 ? "" : " — add more dumps to narrow them down");
    }

    // ---- result actions ----

    private string? SelectedExpression()
    {
        string? text = resultList.SelectedItem?.ToString();
        if (text == null)
        {
            MessageDialog.ShowMessage("Select a result first.", AlertType.INFORMATION);
        }
        return text;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (SelectedExpression() is not { } text)
        {
            return;
        }
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                mc.SetStatus("Copied " + text);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "clipboard");
        }
    }

    private void OnAddToWatchlist(object? sender, RoutedEventArgs e)
    {
        if (SelectedExpression() is not { } text)
        {
            return;
        }
        mc.Watch().AddExpression(text);
        mc.SetTab(MainWindow.Tab.WATCH_LIST);
    }

    private void OnCreateSignature(object? sender, RoutedEventArgs e)
    {
        if (resultList.SelectedItem is not PointerPath path)
        {
            MessageDialog.ShowMessage("Select a result first.", AlertType.INFORMATION);
            return;
        }
        // the element applied first to "main" is the base offset: [[main+BASE]+..]+..
        if (path.Elements.Count == 0 || path.Elements[^1] is not OffsetElement baseOffset)
        {
            return;
        }
        mc.SetTab(MainWindow.Tab.SIGNATURES);
        mc.Signatures().CreateForMainOffset(baseOffset.Offset, path.ToString());
    }

    private void OnMemoryViewer(object? sender, RoutedEventArgs e)
    {
        if (SelectedExpression() is not { } text || !mc.RequireConnection())
        {
            return;
        }
        try
        {
            long addr = mc.Tools().GetEvaluator().Eval(text);
            mc.Memory().SetViewAddress(addr);
            mc.SetTab(MainWindow.Tab.MEMORY_VIEWER);
        }
        catch (Exception ex)
        {
            MessageDialog.ShowMessage("Unable to resolve " + text + ":\n" + ex.Message +
                                      "\n\n(attach to the process first so \"main\" is known)", AlertType.WARNING);
        }
    }
}
