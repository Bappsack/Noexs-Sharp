namespace Noexs.Core;

public sealed class MemoryInfo
{
    public MemoryInfo(long addr, long size, int type, int perm)
    {
        Address = addr;
        Size = size;
        Type = MemoryTypes.ValueOf(type);
        Perm = perm;
    }

    public long Address { get; } //64

    public long Size { get; } //64

    public MemoryType? Type { get; } //32

    public int Perm { get; } //32

    public long NextAddress => Address + Size;

    public bool IsReadable => (Perm & 1) != 0;

    public bool IsWriteable => (Perm & 2) != 0;

    public bool IsExecutable => (Perm & 4) != 0;

    public bool Contains(long addr)
    {
        return Address <= addr && NextAddress > addr;
    }

    public override string ToString()
    {
        return "MemoryInfo{" +
               "addr=0x" + ((ulong)Address).ToString("x") +
               ", size=0x" + ((ulong)Size).ToString("x") +
               ", type=" + Type +
               ", perm=" + Perm +
               '}';
    }
}
