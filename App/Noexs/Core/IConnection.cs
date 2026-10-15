namespace Noexs.Core;

/// <summary>
/// Little-endian framing over a raw byte transport. Implementations only provide the raw
/// byte operations; everything else is a default interface method, like the Java original.
/// </summary>
public interface IConnection : IDisposable
{
    bool Connected { get; }

    /// <summary>How long a read may wait for the console (ms); 0 restores the default.</summary>
    void SetReadTimeout(int ms)
    {
    }

    void WriteCommand(int cmd)
    {
        WriteByte(cmd);
    }

    void Write(byte[] b)
    {
        Write(b, 0, b.Length);
    }

    void Write(byte[] data, int off, int len);

    void WriteLong(long l)
    {
        WriteInt((int)(l & 0xFFFFFFFFL));
        WriteInt((int)((l >> 32) & 0xFFFFFFFFL));
    }

    void WriteInt(int i)
    {
        int s1 = i & 0xFFFF;
        int s2 = (i >> 16) & 0xFFFF;
        byte[] data = [(byte)(s1 & 0xFF), (byte)((s1 >> 8) & 0xFF), (byte)(s2 & 0xFF), (byte)((s2 >> 8) & 0xFF)];
        Write(data);
    }

    void WriteShort(int s)
    {
        byte[] data = [(byte)(s & 0xFF), (byte)((s >> 8) & 0xFF)];
        Write(data);
    }

    void WriteByte(int i);

    Result ReadResult()
    {
        return Result.ValueOf(ReadInt());
    }

    int ReadByte();

    long ReadLong()
    {
        long l1 = ReadUInt();
        long l2 = ReadUInt();
        return l1 | (l2 << 32);
    }

    long ReadUInt()
    {
        return (uint)ReadInt();
    }

    int ReadInt()
    {
        return ReadUShort() | (ReadUShort() << 16);
    }

    int ReadUShort()
    {
        return ReadShort() & 0xFFFF;
    }

    short ReadShort()
    {
        byte[] data = new byte[2];
        int len = ReadFully(data);
        if (len != 2)
        {
            throw new ConnectionException("Unable to fully read data. Expected 2 bytes, but we only read:" + len);
        }
        return (short)((data[0] & 0xFF) | ((data[1] & 0xFF) << 8));
    }

    int Read(byte[] b)
    {
        return Read(b, 0, b.Length);
    }

    /// <returns>bytes read, or a value &lt;= 0 at end of stream</returns>
    int Read(byte[] b, int off, int len);

    int ReadFully(byte[] b)
    {
        return ReadFully(b, 0, b.Length);
    }

    int ReadFully(byte[] b, int off, int len)
    {
        int tmp = len;
        while (tmp > 0)
        {
            int i = Read(b, off, tmp);
            if (i <= 0)
            {
                break;
            }
            off += i;
            tmp -= i;
        }
        return len - tmp;
    }

    void Flush();
}
