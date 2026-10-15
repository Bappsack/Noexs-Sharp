using System.ComponentModel;
using System.Runtime.CompilerServices;
using Noexs.Core;
using Noexs.Util;

namespace Noexs.UI.Models;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class MemoryInfoTableModel
{
    public MemoryInfoTableModel(MemoryInfo info) : this("-", info)
    {
    }

    public MemoryInfoTableModel(string name, MemoryInfo info)
    {
        Name = name;
        Addr = info.Address;
        Size = info.Size;
        Type = info.Type;
        Access = info.Perm;
    }

    public string Name { get; }

    public long Addr { get; }

    public long Size { get; }

    public long End => Addr + Size;

    public MemoryType? Type { get; }

    public int Access { get; }

    // display columns
    public string AddrText => HexUtils.FormatAddress(Addr);

    public string SizeText => HexUtils.FormatSize(Size);

    public string TypeText => Type?.ToString() ?? "";

    public string AccessText => HexUtils.FormatAccess(Access);
}

public sealed class SearchValueModel
{
    private readonly int digits;

    public SearchValueModel(long addr, long prev, long current, int dataSize)
    {
        Addr = addr;
        OldValue = prev;
        NewValue = current;
        digits = dataSize * 2;
    }

    public long Addr { get; }

    public long OldValue { get; }

    public long NewValue { get; }

    public long Diff => NewValue - OldValue;

    private string FormatValue(long value)
    {
        return HexUtils.Pad('0', digits, ((ulong)value).ToString("x"));
    }

    public string AddrText => HexUtils.FormatAddress(Addr);

    public string OldText => FormatValue(OldValue);

    public string NewText => FormatValue(NewValue);

    public string DiffText => (Diff < 0 ? "-" : "") + FormatValue(HexUtils.Abs(Diff));
}

/// <summary>One 16-byte row of the memory viewer.</summary>
public sealed class MemoryViewerTableModel : ObservableModel
{
    private long addr;
    private readonly int[] values = new int[4];

    public void Set(long addr, int memVal1, int memVal2, int memVal3, int memVal4)
    {
        this.addr = addr;
        values[0] = memVal1;
        values[1] = memVal2;
        values[2] = memVal3;
        values[3] = memVal4;
        OnPropertyChanged(string.Empty); // everything changed
    }

    public long Addr => addr;

    private string Ascii(int i)
    {
        int rem = i % 4;
        int j = (values[i / 4] >> (24 - rem * 8)) & 0xFF;
        return FormatChar(j);
    }

    private static string FormatChar(int i)
    {
        if (i < 0x20 || i > 0x7E && i < 0xA1)
        {
            return ".";
        }
        return ((char)i).ToString();
    }

    public string AddrText => HexUtils.FormatAddress(addr);
    public string Value1Text => HexUtils.FormatInt(values[0]);
    public string Value2Text => HexUtils.FormatInt(values[1]);
    public string Value3Text => HexUtils.FormatInt(values[2]);
    public string Value4Text => HexUtils.FormatInt(values[3]);

    public string Ascii00 => Ascii(0x0);
    public string Ascii01 => Ascii(0x1);
    public string Ascii02 => Ascii(0x2);
    public string Ascii03 => Ascii(0x3);
    public string Ascii04 => Ascii(0x4);
    public string Ascii05 => Ascii(0x5);
    public string Ascii06 => Ascii(0x6);
    public string Ascii07 => Ascii(0x7);
    public string Ascii08 => Ascii(0x8);
    public string Ascii09 => Ascii(0x9);
    public string Ascii0A => Ascii(0xA);
    public string Ascii0B => Ascii(0xB);
    public string Ascii0C => Ascii(0xC);
    public string Ascii0D => Ascii(0xD);
    public string Ascii0E => Ascii(0xE);
    public string Ascii0F => Ascii(0xF);
}

public sealed class WatchlistModel : ObservableModel
{
    private bool update;
    private bool locked;
    private string desc = "-";
    private string addr = HexUtils.FormatAddress(0);
    private long value;
    private DataType type = DataType.Int;

    public bool Update
    {
        get => update;
        set
        {
            if (update == value) return;
            update = value;
            OnPropertyChanged();
            if (value)
            {
                Locked = false;
            }
        }
    }

    public bool Locked
    {
        get => locked;
        set
        {
            if (locked == value) return;
            locked = value;
            OnPropertyChanged();
            if (value)
            {
                Update = false;
            }
        }
    }

    public string Desc
    {
        get => desc;
        set
        {
            desc = value;
            OnPropertyChanged();
        }
    }

    public string Addr
    {
        get => addr;
        set
        {
            addr = value;
            OnPropertyChanged();
        }
    }

    public long Value
    {
        get => Interlocked.Read(ref this.value);
        set
        {
            Interlocked.Exchange(ref this.value, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
        }
    }

    public DataType Type
    {
        get => type;
        set
        {
            if (value == null || type == value) return;
            type = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
        }
    }

    /// <summary>Editable hex view of <see cref="Value"/> (the Java UI used a HexSpinner cell).</summary>
    public string ValueText
    {
        get => HexUtils.Pad('0', Size * 2, ((ulong)Value).ToString("X"));
        set
        {
            if (HexUtils.TryParseHex(value, out long v))
            {
                Value = v;
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    public int Size => type.Size;

    public override string ToString()
    {
        return "WatchlistModel{locked=" + locked + ", desc=" + desc + ", addr=" + addr +
               ", value=" + Value + ", type=" + type + '}';
    }
}

/// <summary>
/// One dump in the pointer searcher table: row 1 is searched, the other rows narrow the results
/// down (PointerSearcher's dataGridView1). Address fields are hex text, as typed by the user.
/// </summary>
public sealed class PointerDumpRow : ObservableModel
{
    private string file = "";
    private string mainStart = "";
    private string mainEnd = "";
    private string heapStart = "";
    private string heapEnd = "";
    private string target = "";

    public string File
    {
        get => file;
        set
        {
            file = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileName));
        }
    }

    public string FileName => string.IsNullOrEmpty(file) ? "(click … to choose a dump)" : Path.GetFileName(file);

    public string MainStart
    {
        get => mainStart;
        set
        {
            mainStart = value;
            OnPropertyChanged();
        }
    }

    public string MainEnd
    {
        get => mainEnd;
        set
        {
            mainEnd = value;
            OnPropertyChanged();
        }
    }

    public string HeapStart
    {
        get => heapStart;
        set
        {
            heapStart = value;
            OnPropertyChanged();
        }
    }

    public string HeapEnd
    {
        get => heapEnd;
        set
        {
            heapEnd = value;
            OnPropertyChanged();
        }
    }

    public string Target
    {
        get => target;
        set
        {
            target = value;
            OnPropertyChanged();
        }
    }

    public bool IsBlank => string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(mainStart)
                                                           && string.IsNullOrWhiteSpace(mainEnd)
                                                           && string.IsNullOrWhiteSpace(heapStart)
                                                           && string.IsNullOrWhiteSpace(heapEnd)
                                                           && string.IsNullOrWhiteSpace(target);
}

/// <summary>One memory block stored in a dump (Dump Parser tab).</summary>
public sealed class DumpRegionRow(long address, long size, long filePos, string region)
{
    public long Address { get; } = address;
    public long Size { get; } = size;
    public long FilePos { get; } = filePos;

    public string RamStart => Address.ToString("X");
    public string RamEnd => (Address + Size).ToString("X");
    public string Region { get; } = region;
    public string SizeText => Size.ToString("X");
    public string FilePosText => FilePos.ToString("X");
}
