using Noexs.Core;

namespace Noexs.Dump;

public abstract class DumpRegionSupplier
{
    public abstract long Start { get; }

    public abstract long End { get; }

    public virtual long Size => End - Start;

    /// <returns>the next region, or null when exhausted</returns>
    public abstract DumpRegion? Get();

    public static DumpRegionSupplier CreateSupplier(long start, long end, List<DumpRegion> regions)
    {
        return CreateSupplier(start, end, regions, end - start);
    }

    public static DumpRegionSupplier CreateSupplier(long start, long end, List<DumpRegion> regions, long size)
    {
        return new ListSupplier(start, end, regions, size);
    }

    public static DumpRegionSupplier CreateSupplierFromInfo(Debugger conn, Func<MemoryInfo, bool> filter)
    {
        return new InfoSupplier(conn, filter);
    }

    public static DumpRegionSupplier CreateSupplierFromRange(Debugger conn, long start, long end)
    {
        return new RangeSupplier(conn, start, end);
    }

    private sealed class ListSupplier(long start, long end, List<DumpRegion> regions, long size) : DumpRegionSupplier
    {
        private int i;

        public override long Start => start;

        public override long End => end;

        public override long Size => size;

        public override DumpRegion? Get()
        {
            if (i >= regions.Count)
            {
                return null;
            }
            return regions[i++];
        }
    }

    private sealed class InfoSupplier(Debugger conn, Func<MemoryInfo, bool> filter) : DumpRegionSupplier
    {
        private long start;
        private long end;
        private long size;
        private MemoryInfo[]? info;
        private int i;

        public override long Start
        {
            get
            {
                Init();
                return start;
            }
        }

        public override long End
        {
            get
            {
                Init();
                return end;
            }
        }

        public override long Size
        {
            get
            {
                Init();
                return size;
            }
        }

        private void Init()
        {
            if (info != null)
            {
                return;
            }
            info = conn.Query(0, 10000);
            foreach (MemoryInfo inf in info)
            {
                if (!filter(inf))
                {
                    continue;
                }
                long addr = inf.Address;
                long next = inf.NextAddress;
                if (start == 0)
                {
                    start = addr;
                }
                if (next > end)
                {
                    end = next;
                }
                size += inf.Size;
            }
        }

        public override DumpRegion? Get()
        {
            Init();
            MemoryInfo curr;
            do
            {
                if (i >= info!.Length)
                {
                    return null;
                }
                curr = info[i++];
            } while (!filter(curr));
            return new DumpRegion(curr.Address, curr.NextAddress);
        }
    }

    private sealed class RangeSupplier(Debugger conn, long start, long end) : DumpRegionSupplier
    {
        private MemoryInfo[]? info;
        private int i;

        public override long Start => start;

        public override long End => end;

        public override DumpRegion? Get()
        {
            info ??= conn.Query(start, 10000);
            MemoryInfo curr;
            do
            {
                if (i >= info.Length)
                {
                    return null;
                }
                curr = info[i++];
            } while (!curr.IsReadable || curr.NextAddress < start);
            if (curr.Address >= end)
            {
                return null;
            }
            return new DumpRegion(curr.Address, Math.Min(curr.NextAddress, end));
        }
    }
}
