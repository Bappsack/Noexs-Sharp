using Noexs.Util;

namespace Noexs.Dump;

public sealed class DumpIndex
{
    internal long addr;
    internal long filePos;
    internal long size;

    internal DumpIndex()
    {
    }

    public DumpIndex(long addr, long pos, long size)
    {
        this.addr = addr;
        filePos = pos;
        this.size = size;
    }

    public long Address => addr;

    public long EndAddress => Address + Size;

    public long Size => size;

    public long FilePos => filePos;

    public override string ToString()
    {
        return "DumpIndex{" +
               "addr=" + HexUtils.FormatAddress(addr) +
               ", filePos=" + filePos +
               ", size=" + size +
               '}';
    }

    public override bool Equals(object? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is not DumpIndex dumpIndex) return false;
        return addr == dumpIndex.addr &&
               filePos == dumpIndex.filePos &&
               size == dumpIndex.size;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(addr, filePos, size);
    }
}

public sealed class DumpRegion
{
    public DumpRegion(long start, long end)
    {
        Start = start;
        End = end;
    }

    public long Start { get; }

    public long End { get; }

    public long Size => End - Start;

    public override string ToString()
    {
        return "DumpRegion{" +
               "start=" + HexUtils.FormatAddress(Start) +
               ", end=" + HexUtils.FormatAddress(End) +
               '}';
    }
}

public sealed class DumpRegionFactory
{
    private DumpRegionFactory()
    {
    }

    public long Start { get; private set; }

    public long End { get; private set; }

    public long Length => End - Start;

    public DumpRegionFactory SetLength(long len)
    {
        End = Start + len;
        return this;
    }

    public DumpRegionFactory SetStart(long start)
    {
        Start = start;
        return this;
    }

    public DumpRegionFactory SetEnd(long end)
    {
        End = end;
        return this;
    }

    public static DumpRegionFactory Create()
    {
        return new DumpRegionFactory();
    }

    public DumpRegion Build()
    {
        return new DumpRegion(Start, End);
    }
}
