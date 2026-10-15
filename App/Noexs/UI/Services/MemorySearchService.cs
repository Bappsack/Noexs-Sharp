
using Noexs.Core;
using Noexs.Dump;
using Noexs.IO;
using Noexs.Misc;
using Noexs.UI.Models;
using Noexs.Util;

namespace Noexs.UI.Services;

public sealed class MemorySearchService : BackgroundService<SearchResult>
{

    /// <summary>Speed of the last finished dump, e.g. for comparing USB 2 / USB 3 / Wi-Fi.</summary>
    public static string? LastDumpSummary { get; private set; }

    /// <summary>"1.2 GB in 45.3 s = 27.1 MB/s, USB link 410 MB = 9.0 MB/s (3.0x smaller)".</summary>
    internal static string DumpSummary(long memoryBytes, long linkBytes, double seconds, string transport)
    {
        double s = Math.Max(seconds, 0.001);
        string ratio = linkBytes > 0 ? FormattableString.Invariant($" ({memoryBytes / (double)linkBytes:0.0}x smaller)") : "";
        return FormattableString.Invariant($"{NetUtils.FormatSize(memoryBytes)} in {s:0.0} s = {NetUtils.FormatSize((long)(memoryBytes / s))}/s, ") +
               $"{transport} link {NetUtils.FormatSize(linkBytes)} = {NetUtils.FormatSize((long)(linkBytes / s))}/s{ratio}";
    }
    private DumpRegionSupplier? supplier;
    private Debugger? conn;
    private Func<MappedBuffer, long>? provider;
    private DataType? dataType;
    private SearchType? type;
    private ConditionType? compareType;
    private long knownValue;
    private SearchResult? prevResult;

    public void Clear()
    {
        supplier = null;
        conn = null;
        provider = null;
        type = null;
        compareType = null;
        knownValue = 0;
        prevResult?.Dispose();
        prevResult = null;
    }

    public void SetDataType(DataType type)
    {
        provider = type.Size switch
        {
            1 => b => b.GetByte() & 0xFF,
            2 => b => b.GetShort() & 0xFFFF,
            4 => b => b.GetInt() & 0xFFFFFFFFL,
            8 => b => b.GetLong(),
            _ => throw new NotSupportedException("Invalid/Unsupported dataType:" + type),
        };
        dataType = type;
    }

    public void SetType(SearchType t)
    {
        type = t;
    }

    public void SetCompareType(ConditionType type)
    {
        compareType = type;
    }

    public void SetKnownValue(long value)
    {
        knownValue = value;
    }

    public void SetPrevResult(SearchResult? result)
    {
        prevResult = result;
    }

    public void SetConnection(Debugger conn)
    {
        this.conn = conn;
    }

    public void SetSupplier(DumpRegionSupplier? supplier)
    {
        this.supplier = supplier;
    }

    protected override SearchResult Call(TaskContext ctx)
    {
        return new SearchTask(this, ctx).Call();
    }

    private static IAddressList CreateList()
    {
        try
        {
            return MappedLongList.CreateLongList(NoexesFiles.CreateTempFile("addrs"));
        }
        catch (IOException e)
        {
            Log.Error(e);
            return new ListAddressList();
        }
    }

    /// <summary>Per-run state; the service settings are snapshotted when the run starts.</summary>
    private sealed class SearchTask
    {
        private readonly TaskContext ctx;
        private readonly DumpRegionSupplier? supplier;
        private readonly Debugger conn;
        private readonly Func<MappedBuffer, long> provider;
        private readonly DataType dataType;
        private readonly SearchType type;
        private readonly ConditionType compareType;
        private readonly long knownValue;
        private readonly SearchResult? prevResult;

        private SearchResult res = null!;
        private long curr, total, prevAmt;
        private long lastUpdate;
        private readonly Rolling avg = new(10);

        public SearchTask(MemorySearchService s, TaskContext ctx)
        {
            this.ctx = ctx;
            supplier = s.supplier;
            conn = s.conn ?? throw new InvalidOperationException("No connection");
            provider = s.provider ?? throw new InvalidOperationException("No data type");
            dataType = s.dataType!;
            type = s.type ?? throw new InvalidOperationException("No search type");
            compareType = s.compareType ?? throw new InvalidOperationException("No condition");
            knownValue = s.knownValue;
            prevResult = s.prevResult;
        }

        private bool IsCancelled => ctx.IsCancelled;

        private static long Now => Environment.TickCount64;

        public SearchResult Call()
        {
            res = new SearchResult
            {
                type = type,
                dataType = dataType,
                Prev = prevResult,
            };
            res.addresses = CreateList();
            if (prevResult != null)
            {
                RefineSearch(prevResult);
            }
            else
            {
                FullSearch();
            }
            return res;
        }

        private bool Condition(long value, long prev)
        {
            if (type == SearchType.PREVIOUS)
            {
                return compareType.Compare(value, prev);
            }
            if (type == SearchType.KNOWN)
            {
                return compareType.Compare(value, knownValue);
            }
            if (type == SearchType.DIFFERENT)
            {
                return compareType.Compare(HexUtils.Abs(value - prev), knownValue);
            }
            throw new NotSupportedException("Unsupported condition type:" + type);
        }

        private void Search(SearchResult result, MappedBuffer b, long baseAddress)
        {
            int size = dataType.Size;
            // a trailing partial value (range end not aligned to the data size) is skipped instead of throwing
            while (b.Remaining >= size && !IsCancelled)
            {
                long addr = baseAddress + b.Position;
                long current = provider(b);
                long prevValue = result.Prev == null ? -1 : result.GetPrev(addr);
                if (Condition(current, prevValue))
                {
                    result.addresses.Add(addr);
                    AddAddress(addr);
                }
            }
        }

        private void Update(long done)
        {
            curr = done;
            if (Now - lastUpdate > 500)
            {
                lastUpdate = Now;
                avg.Add(curr - prevAmt);
                prevAmt = curr;
            }
            double average = avg.Average * 2;
            long remaining = total - curr;
            ctx.UpdateMessage($"Refining... ({curr}/{total}) ETA: {TimeUtils.FormatTime((long)(remaining / average * 1000))}");
            ctx.UpdateProgress(curr, total);
        }

        /// <summary>
        /// Re-reads every previously matching address and keeps those that still match.
        /// (The Java version walked the dump and a separate address iterator in lock step, which
        /// skipped the only candidate when there was exactly one and misbehaved past the end.)
        /// </summary>
        private void RefineSearch(SearchResult prev)
        {
            ctx.UpdateMessage("Computing regions...");
            DumpRegionSupplier regions = ComputeRegions(prev);
            res.curr = CreateDump(res, regions);
            if (res.curr == null || IsCancelled)
            {
                return;
            }

            IAddressList addrs = prev.addresses;
            int count = addrs.Count;
            total = count;
            int size = dataType.Size;

            DumpIndex? currentIdx = null;
            MappedBuffer? buffer = null;
            for (int i = 0; i < count && !IsCancelled; i++)
            {
                long addr = addrs[i];
                if (currentIdx == null || addr < currentIdx.Address || addr + size > currentIdx.EndAddress)
                {
                    currentIdx = res.curr.GetIndex(addr);
                    if (currentIdx == null || addr + size > currentIdx.EndAddress)
                    {
                        currentIdx = null;
                        continue; // not readable this time
                    }
                    buffer = res.curr.GetBuffer(currentIdx);
                }
                buffer!.Position = (int)(addr - currentIdx.Address);
                long value = provider(buffer);
                long prevValue = res.type.RequiresPrevious ? res.GetPrev(addr) : -1;
                if (Condition(value, prevValue))
                {
                    res.addresses.Add(addr);
                    AddAddress(addr);
                }
                if ((i & 0xFFFF) == 0)
                {
                    Update(i);
                }
            }
            Update(count);
        }

        private void FullSearch()
        {
            DumpRegionSupplier sup = supplier ?? throw new InvalidOperationException("No region supplier");
            res.curr = CreateDump(res, sup);

            if (IsCancelled || res.curr == null)
            {
                return;
            }

            if (type == SearchType.UNKNOWN)
            {
                (res.addresses as IDisposable)?.Dispose();
                res.addresses = new DumpAddressList(res.curr, res.dataType.Size);
                res.regions = res.curr.GetIndicesAsRegions();
                res.start = sup.Start;
                res.end = sup.End;
                return;
            }

            IReadOnlyList<DumpIndex> indices = res.curr.Indices;
            long read = 0;
            long size = res.curr.GetSize();
            long start = sup.Start;
            foreach (DumpIndex idx in indices)
            {
                if (IsCancelled || idx.Address >= sup.End || idx.EndAddress <= start)
                {
                    continue;
                }
                ctx.UpdateMessage("Searching...");
                MappedBuffer b = res.curr.GetBuffer(idx);
                long addr = idx.Address;
                if (addr < start)
                {
                    b.Position = (int)(start - addr);
                }
                Search(res, b, addr);
                read += b.Position;
                ctx.UpdateProgress(read, size);
            }
        }

        private DumpRegionSupplier ComputeRegions(SearchResult prev)
        {
            if (prev.regions != null)
            {
                return DumpRegionSupplier.CreateSupplier(prev.Start, prev.End, prev.regions,
                    (long)prev.Size * prev.dataType.Size);
            }
            IAddressList addresses = prev.addresses;
            if (addresses.Count == 0)
            {
                return DumpRegionSupplier.CreateSupplier(0, 0, new List<DumpRegion>(), 0);
            }
            MemoryInfo[] infos = conn.Query(prev.Start, 10000);
            var regions = new List<DumpRegion>();
            DumpRegionFactory? factory = null;
            long size = 0;
            long start = long.MaxValue;
            long end = 0;
            MemoryInfo? info = null;
            int count = addresses.Count;
            for (int i = 0; i < count && !IsCancelled; i++)
            {
                ctx.UpdateProgress(i, count);
                long addr = addresses[i];
                if (addr < start)
                {
                    start = addr;
                }
                if (addr > end)
                {
                    end = addr;
                }
                if (factory == null)
                {
                    factory = DumpRegionFactory.Create().SetStart(addr).SetLength(dataType.Size);
                    info = GetInfo(infos, addr);
                }
                else
                {
                    long delta = addr - factory.End;
                    if (delta <= 0x10000 && info != null && info.Contains(addr))
                    {
                        factory.SetEnd(addr + dataType.Size);
                    }
                    else
                    {
                        size += factory.Length;
                        regions.Add(factory.Build());
                        factory = DumpRegionFactory.Create().SetStart(addr).SetLength(dataType.Size);
                        info = GetInfo(infos, addr);
                    }
                }
            }
            if (factory != null)
            {
                size += factory.Length;
                regions.Add(factory.Build());
            }
            ctx.UpdateMessage(regions.Count + " regions computed.");
            return DumpRegionSupplier.CreateSupplier(start, end, regions, size);
        }

        private static MemoryInfo? GetInfo(MemoryInfo[] from, long addr)
        {
            foreach (MemoryInfo info in from)
            {
                if (info.Contains(addr))
                {
                    return info;
                }
            }
            return null;
        }

        private MemoryDump? CreateDump(SearchResult result, DumpRegionSupplier sup)
        {
            LastDumpSummary = null;
            try
            {
                conn.GetTransferStats(); // start the Switch's counters from zero
            }
            catch (ConnectionException e) when (conn.Connected)
            {
                Log.Info("transfer stats: " + e.Message);
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long linkStart = conn.LinkBytesRead;
            bool resume = conn.GetStatus() == DebuggerStatus.PAUSED;

            //pause the game
            conn.Pause();

            long totalSize = sup.Size;
            long lastUpdateTime = 0;
            long prevRead = 0;
            var average = new Rolling(10);
            long read = 0;
            MemoryDump dump;
            DumpOutputStream dout;
            try
            {
                dump = new MemoryDump(result.Location);
                dump.Tid = conn.GetCurrentTitleId();
                dump.Infos.AddRange(conn.Query(0, 10000));
                dout = dump.OpenStream();
            }
            catch (IOException e)
            {
                Log.Error(e);
                return null;
            }

            int failedAddresses = 0;

            while (!IsCancelled)
            {
                DumpRegion? r = sup.Get();
                if (r == null)
                {
                    break;
                }
                long size = r.Size;
                long addr = r.Start;
                while (size > 0 && !IsCancelled)
                {
                    if (Now - lastUpdateTime > 500)
                    {
                        average.Add(read - prevRead);
                        prevRead = read;
                        lastUpdateTime = Now;
                    }
                    int len = (int)Math.Min(size, 2_000_000);
                    dout.SetCurrentAddress(addr);
                    try
                    {
                        conn.Readmem(addr, len, dout);
                    }
                    catch (Exception e)
                    {
                        System.Diagnostics.Debug.WriteLine(e);
                        failedAddresses++;
                    }
                    size -= len;
                    addr += len;
                    read += len;

                    double avgRate = average.Average * 2;
                    long remaining = totalSize - read;
                    ctx.UpdateProgress(read, totalSize);
                    ctx.UpdateMessage($"[S: {failedAddresses}] Dumping - DL: {NetUtils.FormatSize((long)avgRate)}/s" +
                                      $" T: {NetUtils.FormatSize(totalSize)} R: {NetUtils.FormatSize(remaining)}" +
                                      $" ETA: {TimeUtils.FormatTime((long)(remaining / avgRate * 1000))}");
                }
            }
            dout.Close();
            if (resume)
            {
                conn.Resume();
            }
            if (!IsCancelled)
            {
                LastDumpSummary = DumpSummary(read, conn.LinkBytesRead - linkStart, stopwatch.Elapsed.TotalSeconds,
                    conn.Raw() is Noexs.IO.Usb.BulkConnection ? "USB" : "Wi-Fi");
                try
                {
                    Debugger.TransferStats t = conn.GetTransferStats();
                    LastDumpSummary += FormattableString.Invariant(
                        $" | Switch: read {t.ReadNs / 1e9:0.0} s, compress {t.CompressNs / 1e9:0.0} s, waiting for link {t.WaitNs / 1e9:0.0} s") +
                        $" (compression {conn.Compression.Label()}, double buffering {(conn.DoubleBuffering ? "on" : "off")})";
                }
                catch (ConnectionException e) when (conn.Connected)
                {
                    Log.Info("transfer stats: " + e.Message);
                }
                Log.Info("dump: " + LastDumpSummary);
            }
            return IsCancelled ? null : dump;
        }

        private void AddAddress(long addr)
        {
            if (addr < res.start)
            {
                res.start = addr;
            }
            if (addr > res.end)
            {
                res.end = addr;
            }
        }
    }
}
