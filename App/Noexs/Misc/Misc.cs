using Noexs.Core;
using Noexs.Dump;
using Noexs.IO;

namespace Noexs.Misc;

public sealed class NopConnection : IConnection
{
    public static readonly NopConnection INSTANCE = new();

    private NopConnection()
    {
    }

    public bool Connected => false;

    public void WriteByte(int i)
    {
    }

    public void Write(byte[] data, int off, int len)
    {
    }

    public int ReadByte()
    {
        return -1;
    }

    public int Read(byte[] b, int off, int len)
    {
        return -1;
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}

public sealed class OffsetList
{
    private readonly long @base;
    private readonly int dataSize;

    public OffsetList(long @base, int size, int dataSize)
    {
        this.@base = @base;
        Count = size;
        this.dataSize = dataSize;
    }

    public int Count { get; }

    public long this[int index]
    {
        get
        {
            if (index >= Count)
            {
                throw new IndexOutOfRangeException();
            }
            return @base + (long)index * dataSize;
        }
    }
}

/// <summary>Every aligned address of every region in a dump (result of an "Unknown" search).</summary>
public sealed class DumpAddressList : IAddressList
{
    private readonly List<OffsetList> indexList = new();

    public DumpAddressList(MemoryDump dump, int dataSize)
    {
        foreach (DumpIndex idx in dump.Indices)
        {
            long start = idx.Address;
            int size = (int)(idx.EndAddress - start) / dataSize;
            indexList.Add(new OffsetList(start, size, dataSize));
        }
    }

    public long this[int index]
    {
        get
        {
            foreach (OffsetList list in indexList)
            {
                int size = list.Count;
                if (index < size)
                {
                    return list[index];
                }
                index -= size;
            }
            return 0;
        }
    }

    public int Count
    {
        get
        {
            int size = 0;
            foreach (OffsetList list in indexList)
            {
                size += list.Count;
            }
            return size;
        }
    }

    public void Add(long value)
    {
        throw new NotSupportedException();
    }
}

public static class ResultDecoder
{
    private const int GECKO_MODULE_ID = 349;

    private static readonly Dictionary<int, Module> modules = new()
    {
        [GECKO_MODULE_ID] = new Module("TCPGecko", new Dictionary<int, string>
        {
            [1] = "Init Failed",
            [2] = "Socket Failed",
            [3] = "SocketOpt Failed",
            [4] = "Bind Failed",
            [5] = "Listen Failed",
            [6] = "Accept Failed",
            [7] = "General IO Failure",
            [8] = "Invalid Command",
            [9] = "Not Attached",
            [10] = "Already Attached",
            [11] = "Buffer is too small",
        }),
    };

    public static Module? Lookup(Result r)
    {
        return modules.GetValueOrDefault(r.Module);
    }

    public sealed class Module
    {
        private readonly Dictionary<int, string> messages;

        internal Module(string name, Dictionary<int, string> messages)
        {
            Name = name;
            this.messages = messages;
        }

        public string Name { get; }

        public string GetMessage(int desc)
        {
            return messages.TryGetValue(desc, out var msg) ? msg : "UnknownMessage(" + desc + ")";
        }
    }
}
