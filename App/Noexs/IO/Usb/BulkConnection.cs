using Noexs.Core;

namespace Noexs.IO.Usb;

/// <summary>
/// Byte-stream framing over a pair of USB bulk pipes, mirroring the sysmodule's UsbTransport:
/// <list type="bullet">
/// <item>reads always request a large buffer (a multiple of every bulk packet size) and complete
/// on the device's short packet; bytes are then handed out from that buffer, because asking the
/// device for fewer bytes than it sends overflows the transfer;</item>
/// <item>writes are buffered until <see cref="Flush"/>, sent as one transfer, and split in two when
/// the length is a multiple of 64 so the final packet is always short (otherwise the device
/// would wait for more data).</item>
/// </list>
/// </summary>
public abstract class BulkConnection : IConnection
{
    protected const int ReadTransferSize = 0x10000;
    protected const int TimeoutMs = 30_000;
    private const int ShortPacketGranularity = 64;

    private readonly byte[] inBuf = new byte[ReadTransferSize];
    private int inPos, inLen;
    private byte[] outBuf = new byte[0x4000];
    private int outLen;
    private bool broken;

    /// <summary>Receives into buf (at most len bytes). Returns the byte count; throws on failure.</summary>
    protected abstract int TransferIn(byte[] buf, int off, int len);

    /// <summary>Sends exactly len bytes as one bulk transfer; throws on failure.</summary>
    protected abstract void TransferOut(byte[] buf, int off, int len);

    protected abstract bool IsOpen { get; }

    protected abstract void Close();

    /// <summary>Sets the IN pipe timeout (ms) of the underlying device.</summary>
    protected virtual void SetInTimeout(int ms)
    {
    }

    public void SetReadTimeout(int ms)
    {
        SetInTimeout(ms > 0 ? ms : TimeoutMs);
    }

    public bool Connected => !broken && IsOpen;

    private ConnectionException Broken(Exception e)
    {
        broken = true;
        return e as ConnectionException ?? new ConnectionException(e);
    }

    private void EnsureOpen()
    {
        if (!Connected)
        {
            throw new ConnectionException("USB connection closed");
        }
    }

    private void Fill()
    {
        EnsureOpen();
        if (outLen > 0)
        {
            Flush();
        }
        try
        {
            int n = TransferIn(inBuf, 0, inBuf.Length);
            if (n <= 0)
            {
                throw new ConnectionException("USB read returned no data");
            }
            inPos = 0;
            inLen = n;
        }
        catch (Exception e)
        {
            throw Broken(e);
        }
    }

    public int ReadByte()
    {
        if (inPos == inLen)
        {
            Fill();
        }
        return inBuf[inPos++];
    }

    public int Read(byte[] b, int off, int len)
    {
        if (len == 0)
        {
            return 0;
        }
        if (inPos == inLen)
        {
            Fill();
        }
        int n = Math.Min(len, inLen - inPos);
        Buffer.BlockCopy(inBuf, inPos, b, off, n);
        inPos += n;
        return n;
    }

    public void WriteByte(int i)
    {
        Reserve(1);
        outBuf[outLen++] = (byte)i;
    }

    public void Write(byte[] data, int off, int len)
    {
        Reserve(len);
        Buffer.BlockCopy(data, off, outBuf, outLen, len);
        outLen += len;
    }

    private void Reserve(int len)
    {
        EnsureOpen();
        if (outLen + len > outBuf.Length)
        {
            Array.Resize(ref outBuf, Math.Max(outBuf.Length * 2, outLen + len));
        }
    }

    public void Flush()
    {
        if (outLen == 0)
        {
            return;
        }
        EnsureOpen();
        int len = outLen;
        outLen = 0;
        try
        {
            if (len % ShortPacketGranularity == 0)
            {
                TransferOut(outBuf, 0, len - 1);
                TransferOut(outBuf, len - 1, 1);
            }
            else
            {
                TransferOut(outBuf, 0, len);
            }
        }
        catch (Exception e)
        {
            throw Broken(e);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Connected && outLen > 0)
            {
                Flush();
            }
        }
        catch (Exception)
        {
            // closing anyway
        }
        broken = true;
        Close();
    }
}
