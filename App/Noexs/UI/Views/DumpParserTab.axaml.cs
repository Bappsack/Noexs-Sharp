using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Noexs.Core;
using Noexs.Dump;
using Noexs.Misc;
using Noexs.UI.Models;
using Noexs.Util;

namespace Noexs.UI.Views;

/// <summary>
/// Offline inspection of a Noexes .dmp file, after NoExesDumpParse by rydoginator:
/// list the stored memory blocks, convert game addresses to file offsets and back, and
/// evaluate pointer expressions against the dump.
/// </summary>
public partial class DumpParserTab : UserControl, IController
{
    private MainWindow mc = null!;
    private MemoryDump? dump;
    private bool analyzed;
    private bool syncingOffsets;
    private readonly ObservableCollection<DumpRegionRow> regions = new();

    public DumpParserTab()
    {
        InitializeComponent();
        regionGrid.ItemsSource = regions;
        regionGrid.SelectionChanged += (_, _) =>
        {
            if (regionGrid.SelectedItem is DumpRegionRow row)
            {
                SetOffsets(row.Address.ToString("X"), row.FilePos.ToString("X"));
            }
        };
        gameOffset.TextChanged += (_, _) => ConvertOffset(fromGame: true);
        fileOffset.TextChanged += (_, _) => ConvertOffset(fromGame: false);
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
    }

    public void OnConnect()
    {
    }

    public void OnDisconnect()
    {
    }

    public void Stop()
    {
        dump?.Dispose();
        dump = null;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        string? f = await mc.BrowseFile(false, "select Noexs dump file", "NoexsDumpFile", "*.dmp");
        if (f != null)
        {
            Load(f);
        }
    }

    private void OnFromSearch(object? sender, RoutedEventArgs e)
    {
        DumpPicker.Show(fromSearchButton, entry =>
        {
            Load(entry.Path);
            OnAnalyze(sender, e);
        });
    }

    /// <summary>Opens a dump and fills main/heap from its memory map (first/last address of each).</summary>
    public void Load(string path)
    {
        MemoryDump? opened = null;
        try
        {
            opened = new MemoryDump(path, readOnly: true); // validates the NDMP magic
        }
        catch (Exception ex)
        {
            Log.Error(ex, "opening " + path);
            MessageDialog.ShowMessage("Not a valid NoExes RAM Dump!\n" + ex.Message, AlertType.ERROR);
            return;
        }

        dump?.Dispose();
        dump = opened;
        analyzed = false;
        regions.Clear();
        dumpPath.Text = path;
        dumpInfo.Text = $"TID {HexUtils.FormatTitleId(dump.Tid)}" + (TitleDb.Lookup(dump.Tid) is { } game ? "  " + game : "");

        MemoryRegions.Ranges r = MemoryRegions.Detect(dump.Infos);
        if (r.MainStart is { } ms) mainStart.Value = ms;
        if (r.MainEnd is { } me) mainEnd.Value = me;
        if (r.HeapStart is { } hs) heapStart.Value = hs;
        if (r.HeapEnd is { } he) heapEnd.Value = he;
        mc.SetStatus(r.MainStart == null && r.HeapStart == null
            ? "The dump has no memory map; enter main/heap ranges manually"
            : "Loaded " + Path.GetFileName(path));
    }

    private void OnAnalyze(object? sender, RoutedEventArgs e)
    {
        if (dump == null)
        {
            MessageDialog.ShowMessage("Choose a dump file first.", AlertType.WARNING);
            return;
        }
        var errors = new List<string>();
        if (mainEnd.Value <= mainStart.Value)
        {
            errors.Add("Main End must be greater than Main Start");
        }
        if (heapEnd.Value <= heapStart.Value)
        {
            errors.Add("Heap End must be greater than Heap Start");
        }
        if (errors.Count > 0)
        {
            MessageDialog.ShowMessage(string.Join("\n", errors), AlertType.ERROR);
            return;
        }

        regions.Clear();
        foreach (DumpIndex idx in dump.Indices.OrderBy(i => i.Address))
        {
            regions.Add(new DumpRegionRow(idx.Address, idx.Size, idx.FilePos, RegionName(idx.Address)));
        }
        analyzed = true;
        mc.SetStatus($"{regions.Count} memory blocks in dump");
    }

    private string RegionName(long address)
    {
        if (heapStart.Value <= address && address < heapEnd.Value)
        {
            return "heap";
        }
        if (mainStart.Value <= address && address < mainEnd.Value)
        {
            return "main";
        }
        // name it after its memory type when the dump carries a memory map
        MemoryInfo? info = dump?.Infos.FirstOrDefault(i => i.Contains(address));
        return info?.Type?.ToString().ToLowerInvariant() ?? "-";
    }

    private void SetOffsets(string game, string file)
    {
        syncingOffsets = true;
        gameOffset.Text = game;
        fileOffset.Text = file;
        syncingOffsets = false;
        offsetInfo.Text = "";
    }

    /// <summary>Game address ↔ file position, through the dump's index table.</summary>
    private void ConvertOffset(bool fromGame)
    {
        if (syncingOffsets || dump == null)
        {
            return;
        }
        TextBox source = fromGame ? gameOffset : fileOffset;
        TextBox other = fromGame ? fileOffset : gameOffset;
        if (string.IsNullOrWhiteSpace(source.Text))
        {
            return;
        }
        if (!HexUtils.TryParseHex(source.Text, out long value))
        {
            offsetInfo.Text = "not a hex number";
            return;
        }
        foreach (DumpIndex idx in dump.Indices)
        {
            long from = fromGame ? idx.Address : idx.FilePos;
            if (value >= from && value < from + idx.Size)
            {
                long converted = fromGame ? idx.FilePos + (value - idx.Address) : idx.Address + (value - idx.FilePos);
                syncingOffsets = true;
                other.Text = converted.ToString("X");
                syncingOffsets = false;
                offsetInfo.Text = "";
                return;
            }
        }
        offsetInfo.Text = fromGame ? "address is not in the dump" : "offset is not inside a memory block";
    }

    private void OnParse(object? sender, RoutedEventArgs e)
    {
        if (dump == null || !analyzed)
        {
            MessageDialog.ShowMessage("Please analyze your dump first.", AlertType.WARNING);
            return;
        }
        string text = (expression.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return;
        }
        var vars = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = mainStart.Value,
            ["heap"] = heapStart.Value,
        };
        MemoryDump d = dump;
        var evaluator = new ExpressionEvaluator(new DictionaryVariables(vars), addr => ReadInt64(d, addr));
        try
        {
            result.Text = evaluator.Eval(text).ToString("X");
        }
        catch (Exception ex)
        {
            result.Text = "Invalid Syntax";
            mc.SetStatus("Parse failed: " + ex.Message);
        }
    }

    /// <summary>Reads a little-endian 64-bit value from the dump; 0 when the address isn't dumped.</summary>
    private static long ReadInt64(MemoryDump d, long address)
    {
        DumpIndex? idx = d.GetIndex(address);
        if (idx == null || address + 8 > idx.EndAddress)
        {
            return 0;
        }
        MappedBuffer b = d.GetBuffer(idx);
        b.Position = (int)(address - idx.Address);
        return b.GetLong();
    }

    private sealed class DictionaryVariables(Dictionary<string, long> vars) : ExpressionEvaluator.IVariableProvider
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
}
