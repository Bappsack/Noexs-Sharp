using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Noexs.Core;

namespace Noexs.Dump;

/// <summary>
/// The ".dmp" file: a big-endian header (as written by Java's RandomAccessFile) followed by
/// raw little-endian memory regions, an index table and a memory-info table.
/// The format is byte-for-byte compatible with dumps created by the Java client.
/// </summary>
public sealed unsafe class MemoryDump : IDisposable
{
    private const int MAGIC = 0x4E444D50; // "NDMP"

    private readonly FileStream dump;
    private readonly bool readOnly;
    private readonly List<DumpIndex> indices = new();
    private readonly List<MemoryInfo> infos = new();
    private readonly ThreadLocal<DumpIndex?> prev = new(() => null);

    // Old mappings are kept alive until Dispose() because handed-out MappedBuffers may still point into them.
    private readonly object mapLock = new();
    private readonly List<(MemoryMappedFile map, MemoryMappedViewAccessor view)> mappings = new();
    private byte* mappedBase;
    private long mappedLength;

    public MemoryDump(string from, bool readOnly = false)
    {
        this.readOnly = readOnly;
        bool b = File.Exists(from) && new FileInfo(from).Length > 0;
        dump = readOnly
            ? new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            : new FileStream(from, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (b)
            {
                ReadHeader();
            }
            else if (readOnly)
            {
                throw new IOException("File is not a dump! (Empty file)");
            }
            else
            {
                WriteHeader();
            }
        }
        catch (EndOfStreamException e)
        {
            dump.Dispose();
            throw new IOException("File is not a dump! (Truncated)", e);
        }
        catch
        {
            dump.Dispose();
            throw;
        }
    }

    private bool disposed;
    private DumpIndex[]? sortedIndices;

    internal FileStream Stream => dump;

    internal List<DumpIndex> MutableIndices => indices;

    internal void WriteHeader()
    {
        long pos = dump.Length;
        dump.Position = 0;
        WriteInt(MAGIC);
        WriteLong(Tid); // TID
        WriteInt(infos.Count); // info count
        WriteLong(0); // mem-info pointer
        WriteInt(indices.Count); // index count
        WriteLong(pos); // pointer to index data
        dump.Position = pos;
        foreach (DumpIndex idx in indices)
        {
            WriteLong(idx.addr);
            WriteLong(idx.filePos);
            WriteLong(idx.size);
        }
        pos = dump.Position;
        foreach (MemoryInfo info in infos)
        {
            WriteLong(info.Address);
            WriteLong(info.Size);
            WriteInt(info.Type.HasValue ? (int)info.Type.Value : 0);
            WriteInt(info.Perm);
        }
        dump.Position = 0x10; // mem-info pointer (again)
        WriteLong(pos);

        // make the header visible to other readers of the file (Pointer Search / Dump Parser)
        dump.Flush();
        dump.Position = 0x32; // seek back to data
    }

    private void ReadHeader()
    {
        dump.Position = 0;
        if (ReadInt() != MAGIC)
        {
            throw new IOException("File is not a dump! (Invalid magic)");
        }
        Tid = ReadLong(); // TID

        int infoCount = ReadInt();
        long infoPtr = ReadLong();

        int idxCount = ReadInt();
        long idxPtr = ReadLong();
        long dataPtr = dump.Position;
        dump.Position = idxPtr;
        for (int i = 0; i < idxCount; i++)
        {
            long addr = ReadLong();
            long pos = ReadLong();
            long size = ReadLong();
            indices.Add(new DumpIndex(addr, pos, size));
        }

        dump.Position = infoPtr;
        for (int i = 0; i < infoCount; i++)
        {
            long addr = ReadLong();
            long size = ReadLong();
            int type = ReadInt();
            int perm = ReadInt();
            infos.Add(new MemoryInfo(addr, size, type, perm));
        }
        dump.Position = dataPtr;
    }

    public DumpOutputStream OpenStream()
    {
        return new DumpOutputStream(this, indices, dump);
    }

    /// <summary>Writes the header (unless read-only) and releases the file. Safe to call twice; never throws.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (!readOnly)
        {
            try
            {
                WriteHeader();
            }
            catch (Exception e)
            {
                Noexs.Util.Log.Error(e, "writing dump header");
            }
        }
        lock (mapLock)
        {
            foreach (var (map, view) in mappings)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
                map.Dispose();
            }
            mappings.Clear();
            mappedBase = null;
            mappedLength = 0;
        }
        indices.Clear();
        sortedIndices = null;
        dump.Dispose();
    }

    public List<MemoryInfo> Infos => infos;

    public IReadOnlyList<DumpIndex> Indices => indices;

    public List<DumpRegion> GetIndicesAsRegions()
    {
        var res = new List<DumpRegion>();
        foreach (DumpIndex idx in indices)
        {
            res.Add(new DumpRegion(idx.Address, idx.EndAddress));
        }
        return res;
    }

    public long GetSize()
    {
        return dump.Length;
    }

    public long GetStart()
    {
        long min = long.MaxValue;
        foreach (DumpIndex idx in Indices)
        {
            long addr = idx.addr;
            if (addr < min)
            {
                min = addr;
            }
        }
        return min;
    }

    public long GetEnd()
    {
        long max = 0;
        foreach (DumpIndex idx in Indices)
        {
            long addr = idx.addr + idx.size;
            if (addr >= max)
            {
                max = addr;
            }
        }
        return max;
    }

    private byte* EnsureMapped(long end)
    {
        lock (mapLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mappedBase != null && end <= mappedLength)
            {
                return mappedBase;
            }
            dump.Flush();
            long length = dump.Length;
            if (end > length)
            {
                throw new IOException("Dump index points past the end of the file");
            }
            var map = MemoryMappedFile.CreateFromFile(dump, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: true);
            var view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            mappings.Add((map, view));
            mappedBase = p + view.PointerOffset;
            mappedLength = length;
            return mappedBase;
        }
    }

    public MappedBuffer GetBuffer(DumpIndex idx)
    {
        if (idx.size == 0)
        {
            return new MappedBuffer(null, 0);
        }
        byte* basePtr = EnsureMapped(idx.filePos + idx.size);
        return new MappedBuffer(basePtr + idx.filePos, checked((int)idx.size));
    }

    public MappedBuffer GetBuffer(long addr)
    {
        DumpIndex idx = GetIndex(addr)
                        ?? throw new IOException("Address " + addr.ToString("X") + " is not part of the dump");
        MappedBuffer buffer = GetBuffer(idx);
        buffer.Position = (int)(addr - idx.addr);
        return buffer;
    }

    public long GetValue(long addr, int size)
    {
        MappedBuffer buffer = GetBuffer(addr);
        return size switch
        {
            1 => buffer.GetByte() & 0xFF,
            2 => buffer.GetShort() & 0xFFFF,
            4 => buffer.GetInt() & 0xFFFFFFFFL,
            8 => buffer.GetLong(),
            _ => throw new NotSupportedException("invalid size:" + size),
        };
    }

    public long Tid { get; set; }

    public DumpIndex? GetIndex(long addr)
    {
        DumpIndex? p = prev.Value;
        if (p != null && addr >= p.addr && addr < p.addr + p.size)
        {
            return p;
        }
        // binary search over the indices ordered by address (built lazily, rebuilt if indices were added)
        DumpIndex[]? sorted = sortedIndices;
        if (sorted == null || sorted.Length != indices.Count)
        {
            sorted = indices.ToArray();
            Array.Sort(sorted, (a, b) => a.addr.CompareTo(b.addr));
            sortedIndices = sorted;
        }
        int lo = 0, hi = sorted.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            DumpIndex idx = sorted[mid];
            if (addr < idx.addr)
            {
                hi = mid - 1;
            }
            else if (addr >= idx.addr + idx.size)
            {
                lo = mid + 1;
            }
            else
            {
                prev.Value = idx;
                return idx;
            }
        }
        return null;
    }

    // RandomAccessFile is big-endian; keep that for the header so files stay compatible.
    private void WriteInt(int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        dump.Write(b);
    }

    private void WriteLong(long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        dump.Write(b);
    }

    private int ReadInt()
    {
        Span<byte> b = stackalloc byte[4];
        dump.ReadExactly(b);
        return BinaryPrimitives.ReadInt32BigEndian(b);
    }

    private long ReadLong()
    {
        Span<byte> b = stackalloc byte[8];
        dump.ReadExactly(b);
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }
}

public sealed class DumpOutputStream : Stream
{
    private DumpIndex? curr;
    private readonly List<DumpIndex> indices;
    private FileStream? dataFile;
    private readonly MemoryDump from;

    internal DumpOutputStream(MemoryDump from, List<DumpIndex> indices, FileStream data)
    {
        this.from = from;
        this.indices = indices;
        dataFile = data;
    }

    private FileStream Data => dataFile ?? throw new ObjectDisposedException(nameof(DumpOutputStream));

    public void SetCurrentAddress(long addr)
    {
        if (curr != null)
        {
            curr.size = Data.Position - curr.filePos;
            indices.Add(curr);
        }
        curr = new DumpIndex
        {
            addr = addr,
            filePos = Data.Position,
        };
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Data.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Data.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Data.WriteByte(value);
    }

    public override void Close()
    {
        if (dataFile != null)
        {
            if (curr != null)
            {
                curr.size = dataFile.Position - curr.filePos;
                indices.Add(curr);
                curr = null;
            }
            from.WriteHeader();
            dataFile = null;
        }
        base.Close();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
