using Noexs.Dump;
using Noexs.IO;
using Noexs.UI.Models;

namespace Noexs.UI.Services;

public sealed class SearchResult : IDisposable
{
    private const int PAGE_SIZE = 1024;

    internal IAddressList addresses = new ListAddressList();
    internal List<DumpRegion>? regions;
    internal DataType dataType = DataType.Int;
    internal SearchType type = SearchType.KNOWN;
    internal long start = long.MaxValue; // lowest matching address (Java left this at 0)
    internal long end;
    internal MemoryDump? curr;

    public SearchResult()
    {
        Location = NoexesFiles.CreateTempFile("dmp");
    }

    public string Location { get; }

    public IAddressList Addresses => addresses;

    public long Start => start;

    public long End => end;

    public DataType DataType => dataType;

    public SearchType Type => type;

    public SearchResult? Prev { get; set; }

    public long GetCurr(long addr)
    {
        if (curr == null)
        {
            throw new IOException("Search result has no dump");
        }
        return curr.GetValue(addr, dataType.Size);
    }

    public long GetPrev(long addr)
    {
        if (Prev == null)
        {
            return GetCurr(addr);
        }
        return Prev.GetCurr(addr);
    }

    public int Size => addresses.Count;

    public List<long> GetPage(int idx)
    {
        int from = PAGE_SIZE * idx;
        int to = Math.Min(Size, PAGE_SIZE * (idx + 1));
        var res = new List<long>(Math.Max(0, to - from));
        for (int i = from; i < to; i++)
        {
            res.Add(addresses[i]);
        }
        return res;
    }

    public int GetPageCount()
    {
        int size = Size;
        if (size == 0)
        {
            return 0;
        }
        if (size % PAGE_SIZE != 0)
        {
            size += PAGE_SIZE;
        }
        return size / PAGE_SIZE;
    }

    private bool disposed;

    /// <summary>Releases this result and its predecessors. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        curr?.Dispose();
        Prev?.Dispose();
        (addresses as IDisposable)?.Dispose();
    }
}
