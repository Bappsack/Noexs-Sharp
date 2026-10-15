namespace Noexs.UI.Models;

// The Java enums carried display strings and behaviour, so they are modelled as classes with
// fixed instances; ToString() is what ComboBoxes display.

public sealed class DataType
{
    public static readonly DataType Byte = new("BYTE", "8 bit", 1);
    public static readonly DataType Short = new("SHORT", "16 bit", 2);
    public static readonly DataType Int = new("INT", "32 bit", 4);
    public static readonly DataType Long = new("LONG", "64 bit", 8);

    public static DataType[] Values { get; } = [Byte, Short, Int, Long];

    private readonly string str;

    private DataType(string name, string str, int bytes)
    {
        Name = name;
        this.str = str;
        Size = bytes;
    }

    /// <summary>Java enum constant name; used for JSON compatibility.</summary>
    public string Name { get; }

    public int Size { get; }

    public override string ToString()
    {
        return str;
    }

    public static DataType? FromName(string? name)
    {
        return Values.FirstOrDefault(v => v.Name == name);
    }
}

public sealed class ConditionType
{
    public static readonly ConditionType EQUALS = new("Equals", "==", (a, b) => a == b);
    public static readonly ConditionType NOT_EQUAL = new("Not Equal", "!=", (a, b) => a != b);
    public static readonly ConditionType LESS_THAN = new("Less Than", "<", (a, b) => a < b);
    public static readonly ConditionType LESS_THAN_OR_EQUAL = new("Less Than or Equal", "<=", (a, b) => a <= b);
    public static readonly ConditionType GREATER_THAN = new("Greater Than", ">", (a, b) => a > b);
    public static readonly ConditionType GREATER_OR_EQUAL = new("Greater or Equal", ">=", (a, b) => a >= b);

    public static ConditionType[] Values { get; } =
        [EQUALS, NOT_EQUAL, LESS_THAN, LESS_THAN_OR_EQUAL, GREATER_THAN, GREATER_OR_EQUAL];

    private readonly string str;
    private readonly Func<long, long, bool> compare;

    private ConditionType(string str, string op, Func<long, long, bool> compare)
    {
        this.str = str;
        Operator = op;
        this.compare = compare;
    }

    public string Operator { get; }

    public bool Compare(long value, long other)
    {
        return compare(value, other);
    }

    public override string ToString()
    {
        return str;
    }
}

public enum ConnectionType
{
    USB,
    NETWORK,
}

public sealed class RangeType
{
    public static readonly RangeType ALL = new("All (R/W)");
    public static readonly RangeType RANGE = new("Range");
    public static readonly RangeType HEAP = new("Heap");
    public static readonly RangeType TLS = new("Thread Local Storage");

    public static RangeType[] Values { get; } = [ALL, RANGE, HEAP, TLS];

    private readonly string str;

    private RangeType(string str)
    {
        this.str = str;
    }

    public override string ToString()
    {
        return str;
    }
}

public sealed class SearchType
{
    public static readonly SearchType UNKNOWN = new("Unknown");
    public static readonly SearchType PREVIOUS = new("Previous");
    public static readonly SearchType KNOWN = new("Known");
    public static readonly SearchType DIFFERENT = new("Different");

    public static SearchType[] Values { get; } = [UNKNOWN, PREVIOUS, KNOWN, DIFFERENT];

    private readonly string readable;

    private SearchType(string str)
    {
        readable = str;
    }

    public bool RequiresPrevious => this == PREVIOUS || this == DIFFERENT;

    public override string ToString()
    {
        return readable;
    }
}
