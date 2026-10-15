using System.Buffers.Binary;

namespace Noexs.Dump;

/// <summary>
/// A little-endian read cursor over memory-mapped dump data (stands in for java.nio.ByteBuffer).
/// Only valid while the owning <see cref="MemoryDump"/> is open.
/// </summary>
public sealed unsafe class MappedBuffer
{
    private readonly byte* ptr;

    internal MappedBuffer(byte* ptr, int capacity)
    {
        this.ptr = ptr;
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Position { get; set; }

    public int Remaining => Capacity - Position;

    public bool HasRemaining => Position < Capacity;

    public ReadOnlySpan<byte> Span => new(ptr, Capacity);

    private ReadOnlySpan<byte> Take(int n)
    {
        if (Position < 0 || Position + n > Capacity)
        {
            throw new InvalidOperationException("Buffer underflow");
        }
        var span = new ReadOnlySpan<byte>(ptr + Position, n);
        Position += n;
        return span;
    }

    public byte GetByte()
    {
        return Take(1)[0];
    }

    public short GetShort()
    {
        return BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    }

    public int GetInt()
    {
        return BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    }

    public long GetLong()
    {
        return BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    }
}
