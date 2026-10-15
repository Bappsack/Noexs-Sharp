using System.IO.MemoryMappedFiles;

namespace Noexs.IO;

/// <summary>Minimal list of addresses (the Java code used java.util.List&lt;Long&gt;).</summary>
public interface IAddressList
{
    int Count { get; }

    long this[int index] { get; }

    void Add(long value);
}

public sealed class ListAddressList : IAddressList
{
    private readonly List<long> list = new();

    public int Count => list.Count;

    public long this[int index] => list[index];

    public void Add(long value)
    {
        list.Add(value);
    }
}

/// <summary>
/// An append-only list of longs backed by a memory-mapped temp file, mapped in 50MB chunks.
/// </summary>
public sealed unsafe class MappedLongList : IAddressList, IDisposable
{
    private const long BUFFER_SIZE = 1024 * 1024 * 50; //50MB
    private const long DATA_SIZE = sizeof(long);

    private readonly FileStream file;
    private readonly object growLock = new();
    private readonly List<MemoryMappedFile> maps = new();
    private readonly List<MemoryMappedViewAccessor> views = new();
    private volatile IntPtr[] buffers = [];
    private int size;
    private bool disposed;

    public MappedLongList(FileStream file)
    {
        this.file = file;
    }

    public static MappedLongList CreateLongList(string path)
    {
        return new MappedLongList(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
    }

    private static long WrapIndex(int index)
    {
        return index * DATA_SIZE % BUFFER_SIZE;
    }

    private void CheckSize(int index)
    {
        lock (growLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = buffers;
            if (index < current.Length)
            {
                return;
            }
            var list = new List<IntPtr>(current);
            while (index >= list.Count)
            {
                long offset = list.Count * BUFFER_SIZE;
                var map = MemoryMappedFile.CreateFromFile(file, null, offset + BUFFER_SIZE,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                var view = map.CreateViewAccessor(offset, BUFFER_SIZE, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                maps.Add(map);
                views.Add(view);
                list.Add((IntPtr)(p + view.PointerOffset));
            }
            buffers = list.ToArray();
        }
    }

    private byte* GetBuffer(int idx)
    {
        int bufferIndex = (int)(idx * DATA_SIZE / BUFFER_SIZE);
        var current = buffers;
        if (bufferIndex >= current.Length)
        {
            CheckSize(bufferIndex);
            current = buffers;
        }
        return (byte*)current[bufferIndex];
    }

    public long this[int index]
    {
        get => *(long*)(GetBuffer(index) + WrapIndex(index));
        set => *(long*)(GetBuffer(index) + WrapIndex(index)) = value;
    }

    public void Add(long value)
    {
        int index = size++;
        this[index] = value;
    }

    public int Count => size;

    public void Dispose()
    {
        lock (growLock)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (var view in views)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
            }
            foreach (var map in maps)
            {
                map.Dispose();
            }
            views.Clear();
            maps.Clear();
            buffers = [];
        }
        file.Dispose();
    }
}
