using Noexs.Util;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Noexs.Core;
using Noexs.Dump;
using Noexs.UI.Models;
using Noexs.UI.Services;

namespace Noexs.UI.Views;

public partial class SearchTab : UserControl, IController
{
    private MainWindow mc = null!;

    private SearchResult? result;

    private BackgroundService? runningService;
    private readonly MemorySearchService searchService = new();

    private readonly ObservableCollection<SearchValueModel> resultList = new();

    private int currentPage;

    public SearchTab()
    {
        InitializeComponent();

        searchType.ItemsSource = RangeType.Values;
        searchType.SelectionChanged += (_, _) =>
        {
            bool b = searchType.SelectedItem != RangeType.RANGE;
            searchStart.IsEnabled = !b;
            searchEnd.IsEnabled = !b;
        };
        searchType.SelectedItem = RangeType.ALL;

        dataTypeDropdown.ItemsSource = DataType.Values;
        dataTypeDropdown.SelectedItem = DataType.Int; // Default is 32-bit
        dataTypeDropdown.SelectionChanged += (_, _) =>
        {
            if (dataTypeDropdown.SelectedItem is not DataType newValue) return;
            int size = newValue.Size * 2;
            knownValue.SetSize(size);
            pokeValue.SetSize(size);
        };

        searchConditionTypeDropdown.ItemsSource = SearchType.Values;
        searchConditionTypeDropdown.SelectedItem = SearchType.KNOWN; // Default is SPEC Value
        searchConditionTypeDropdown.SelectionChanged += (_, _) =>
        {
            bool b = searchConditionTypeDropdown.SelectedItem == SearchType.UNKNOWN;
            knownValue.IsEnabled = !b;
            searchConditionDropdown.IsEnabled = !b;
            if (b)
            {
                searchConditionDropdown.SelectedItem = ConditionType.EQUALS;
            }
            UpdateCondition();
        };

        searchConditionDropdown.ItemsSource = ConditionType.Values;
        searchConditionDropdown.SelectedItem = ConditionType.EQUALS;
        searchConditionDropdown.SelectionChanged += (_, _) => UpdateCondition();

        knownValue.ValueChanged += (_, _) => UpdateCondition();
        knownValue.TextChanged += (_, _) => UpdateCondition();

        searchResults.ItemsSource = resultList;
        searchResults.SelectionChanged += (_, _) =>
        {
            if (searchResults.SelectedItem is SearchValueModel newValue)
            {
                pokeValue.Value = newValue.NewValue;
            }
        };

        var memoryView = new MenuItem { Header = "Memory Viewer" };
        memoryView.Click += (_, _) =>
        {
            if (searchResults.SelectedItem is not SearchValueModel m) return;
            mc.Guard(() =>
            {
                mc.Memory().SetViewAddress(m.Addr);
                mc.SetTab(MainWindow.Tab.MEMORY_VIEWER);
            });
        };
        var watchList = new MenuItem { Header = "Watch List" };
        watchList.Click += (_, _) =>
        {
            if (searchResults.SelectedItem is not SearchValueModel m) return;
            mc.Watch().AddAddr(m.Addr);
            mc.SetTab(MainWindow.Tab.WATCH_LIST);
        };
        var pointerTarget = new MenuItem { Header = "Pointer Search (Target)" };
        pointerTarget.Click += (_, _) =>
        {
            if (searchResults.SelectedItem is not SearchValueModel m) return;
            mc.Pointer().SetTarget(m.Addr);
            mc.SetTab(MainWindow.Tab.POINTER_SEARCH);
        };
        var cm = new ContextMenu();
        cm.Items.Add(memoryView);
        cm.Items.Add(watchList);
        cm.Items.Add(pointerTarget);
        searchResults.ContextMenu = cm;

        searchService.MessageChanged += msg => mc.SetStatus(msg);
        UpdateCondition();
    }

    private void UpdateCondition()
    {
        string value = knownValue.GetValueAsString();
        if (searchConditionTypeDropdown.SelectedItem is not SearchType type
            || searchConditionDropdown.SelectedItem is not ConditionType condition)
        {
            conditionLabel.Text = "Invalid/Unknown condition!";
            return;
        }

        const string currentName = "CURRENT_VALUE";
        const string prevName = "PREV_VALUE";
        string left = currentName;
        string cmp = condition.Operator;
        string right;
        if (type == SearchType.UNKNOWN)
        {
            right = "*";
        }
        else if (type == SearchType.PREVIOUS)
        {
            right = prevName;
        }
        else if (type == SearchType.KNOWN)
        {
            right = value;
        }
        else if (type == SearchType.DIFFERENT)
        {
            left = "|" + currentName + " - " + prevName + "|";
            right = value;
        }
        else
        {
            conditionLabel.Text = "Invalid/Unknown condition!";
            return;
        }
        conditionLabel.Text = left + " " + cmp + " " + right;
    }

    public void SetSearchRange(long start, long end)
    {
        SetStart(start);
        SetEnd(end);
    }

    public void SetStart(long start)
    {
        searchStart.Value = start;
        searchType.SelectedItem = RangeType.RANGE;
    }

    public void SetEnd(long end)
    {
        searchEnd.Value = end;
        searchType.SelectedItem = RangeType.RANGE;
    }

    private void ConvertToHex(object? sender, RoutedEventArgs e)
    {
        if (ulong.TryParse(knownValue.GetValueAsString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong l))
        {
            knownValue.Value = (long)l;
        }
    }

    private void Poke(object? sender, RoutedEventArgs e)
    {
        if (!mc.RequireConnection()) return;
        mc.Guard(Poke);
    }

    private void Poke()
    {
        if (IsServiceRunning())
        {
            MessageDialog.ShowMessage("Please wait for your previous task to finish!", AlertType.WARNING);
            return;
        }
        var models = searchResults.SelectedItems.OfType<SearchValueModel>().ToList();
        long value = pokeValue.Value;
        if (dataTypeDropdown.SelectedItem is not DataType t) return;
        Debugger debugger = mc.GetDebugger();
        foreach (SearchValueModel m in models)
        {
            debugger.Poke(t, m.Addr, value);
        }
    }

    private void OnPageLeft(object? sender, RoutedEventArgs e)
    {
        currentPage--;
        UpdatePageInfo();
    }

    private void OnPageRight(object? sender, RoutedEventArgs e)
    {
        currentPage++;
        UpdatePageInfo();
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
    }

    public void Stop()
    {
        searchService.Cancel();
    }

    public void OnConnect()
    {
        IsEnabled = true;
    }

    public void OnDisconnect()
    {
        IsEnabled = false;
    }

    private void OnStartAction(object? sender, RoutedEventArgs e)
    {
        if (!mc.RequireConnection()) return;
        mc.Guard(OnStartAction);
    }

    private void OnStartAction()
    {
        if (IsServiceRunning())
        {
            MessageDialog.ShowMessage("Please wait for your current task to complete!", AlertType.WARNING);
            return;
        }
        if (dataTypeDropdown.SelectedItem is not DataType dataType
            || searchConditionTypeDropdown.SelectedItem is not SearchType type
            || searchConditionDropdown.SelectedItem is not ConditionType compareType)
        {
            return;
        }
        long known = knownValue.Value;

        searchService.SetConnection(mc.GetDebugger());
        searchService.SetSupplier(GetDumpRegionSupplier(mc.GetDebugger()));

        InitSearch(type, compareType, dataType, known);
    }

    private void OnRestartAction(object? sender, RoutedEventArgs e)
    {
        if (IsServiceRunning())
        {
            MessageDialog.ShowMessage("Please wait for your current search to complete (or cancel it)!", AlertType.WARNING);
            return;
        }
        searchService.Clear();
        mc.UnbindProgress();
        mc.SetProgress(0);
        result?.Dispose();
        result = null;
        currentPage = 0;
        UpdatePageInfo();
        mc.SetStatus("Search result cleared!");
    }

    private void OnCancelAction(object? sender, RoutedEventArgs e)
    {
        if (runningService == null)
        {
            MessageDialog.ShowMessage("No search has been run!", AlertType.INFORMATION);
            return;
        }
        if (!runningService.IsRunning)
        {
            MessageDialog.ShowMessage("No search is running!", AlertType.WARNING);
        }
        if (runningService.Cancel())
        {
            mc.UnbindProgress();
            mc.SetStatus("Search Canceled");
            searchOptions.IsEnabled = true;
        }
    }

    private void OnUndoAction(object? sender, RoutedEventArgs e)
    {
        if (IsServiceRunning())
        {
            MessageDialog.ShowMessage("Please cancel/wait for the current task before undoing a search!", AlertType.WARNING);
            return;
        }
        if (result == null)
        {
            MessageDialog.ShowMessage("Please preform a search before trying to undo it!", AlertType.WARNING);
            return;
        }
        if (result.Prev == null)
        {
            MessageDialog.ShowMessage("No previous search exists!", AlertType.WARNING);
            return;
        }
        SearchResult prev = result.Prev;
        result.Prev = null; //to prevent the result from closing the previous one
        result.Dispose();
        result = prev;
        searchService.SetPrevResult(result);
        UpdatePageInfo();
    }

    private void InitSearch(SearchType type, ConditionType compareType, DataType dataType, long known)
    {
        searchService.SetType(type);
        searchService.SetCompareType(compareType);
        searchService.SetDataType(dataType);
        searchService.SetKnownValue(known);
        string conditionText = conditionLabel.Text ?? "";

        searchService.OnSucceeded = v =>
        {
            result = v;
            DumpHistory.Add(result, result.Prev != null, conditionText);
            searchService.SetPrevResult(result);
            // short enough for the status bar; the dump details are in its tooltip and the log
            mc.SetStatus($"Done! {result.Size:N0} results"
                         + (MemorySearchService.LastDumpSeconds is { } s ? FormattableString.Invariant($" (dump {s:0.0} s)") : ""),
                MemorySearchService.LastDumpDetails);
            resultList.Clear();
            currentPage = 0;
            searchOptions.IsEnabled = true;
            if (result.Type == SearchType.UNKNOWN)
            {
                searchConditionTypeDropdown.SelectedItem = SearchType.PREVIOUS;
                searchConditionDropdown.SelectedItem = ConditionType.NOT_EQUAL;
            }
            UpdatePageInfo();
        };

        searchService.OnFailed = t =>
        {
            searchService.SetPrevResult(null);
            mc.SetStatus("Search failed!");
            Log.Error(t);
            MessageDialog.ShowMessage(t);
            searchOptions.IsEnabled = true;
        };
        searchOptions.IsEnabled = false;
        InitService(searchService);
    }

    private void UpdatePageInfo()
    {
        int maxPages = result?.GetPageCount() ?? 0;
        int size = result?.Size ?? 0;
        pageLeft.IsEnabled = currentPage != 0;
        pageRight.IsEnabled = currentPage < maxPages - 1;
        pageLabel.Text = maxPages == 0
            ? "Page: 0/0 (" + size + ")"
            : "Page: " + (currentPage + 1) + "/" + maxPages + " (" + size + ")";
        resultList.Clear();

        if (result != null)
        {
            int dataSize = result.DataType.Size;
            foreach (long addr in result.GetPage(currentPage))
            {
                try
                {
                    resultList.Add(new SearchValueModel(addr, result.GetPrev(addr), result.GetCurr(addr), dataSize));
                }
                catch (Exception ex)
                {
                    // e.g. the dump was closed/deleted underneath us; show what we have
                    Log.Error(ex);
                    break;
                }
            }
        }
    }

    private void InitService(MemorySearchService service)
    {
        mc.BindProgress(service);
        runningService = service;
        service.Restart();
    }

    private bool IsServiceRunning()
    {
        return runningService != null && runningService.IsRunning;
    }

    private DumpRegionSupplier? GetDumpRegionSupplier(Debugger conn)
    {
        if (result != null && result.Size > 0)
        {
            return DumpRegionSupplier.CreateSupplierFromRange(conn, result.Start, result.End);
        }
        object? t = searchType.SelectedItem;
        if (t == RangeType.RANGE)
        {
            long start = searchStart.Value;
            long end = searchEnd.Value;
            return DumpRegionSupplier.CreateSupplierFromRange(conn, start, end);
        }
        if (t == RangeType.ALL)
        {
            return DumpRegionSupplier.CreateSupplierFromInfo(conn, info => info.IsReadable && info.IsWriteable);
        }
        if (t == RangeType.HEAP)
        {
            return DumpRegionSupplier.CreateSupplierFromInfo(conn, info => info.IsReadable && info.Type == MemoryType.HEAP);
        }
        if (t == RangeType.TLS)
        {
            return DumpRegionSupplier.CreateSupplierFromInfo(conn, info => info.IsReadable && info.Type == MemoryType.THREAD_LOCAL);
        }
        return null;
    }
}
