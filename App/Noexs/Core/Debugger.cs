using System.Buffers.Binary;
using Noexs.Misc;
using Noexs.UI;
using Noexs.UI.Models;
using Noexs.Util;
using static Noexs.Core.Commands;

namespace Noexs.Core;

public sealed class Debugger : IDisposable
{
    private readonly IConnection conn;
    private MemoryInfo? prev;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private int protocolVersion;

    /// <summary>App and sysmodule must have the same major version (no compatibility with other versions).</summary>
    public const int PROTOCOL_MAJOR = NoexsApplication.VERSION_MAJOR;

    public Debugger(IConnection conn)
    {
        this.conn = conn;
    }

    /// <summary>Sysmodule version reported by the last status command (e.g. "2.0.0").</summary>
    public string? ServerVersion { get; private set; }

    public IConnection Raw()
    {
        return conn;
    }

    private void Acquire()
    {
        semaphore.Wait();
    }

    private void Release()
    {
        semaphore.Release();
    }

    public DebuggerStatus? GetStatus()
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_STATUS);
            conn.Flush();
            int status = conn.ReadByte();
            int major = conn.ReadByte();
            int minor = conn.ReadByte();
            int patch = conn.ReadByte();
            protocolVersion = (major << 16) | (minor << 8);
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException("This is impossible, so you've done something terribly wrong", rc);
            }
            ServerVersion = major + "." + minor + "." + patch;
            if (major != PROTOCOL_MAJOR)
            {
                throw new ConnectionException($"The noexs sysmodule on the Switch is version {ServerVersion}, but this App " +
                                              $"needs {PROTOCOL_MAJOR}.x. Install the sysmodule that belongs to this App.");
            }
            protocolVersion |= patch;
            return DebuggerStatus.ForId(status);
        }
        finally
        {
            Release();
        }
    }

    public void Poke(DataType type, long addr, long value)
    {
        switch (type.Size)
        {
            case 1:
                Poke8(addr, (int)value);
                break;
            case 2:
                Poke16(addr, (int)value);
                break;
            case 4:
                Poke32(addr, (int)value);
                break;
            case 8:
                Poke64(addr, value);
                break;
        }
    }

    public void Poke8(long addr, int value)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_POKE8);
            conn.WriteLong(addr);
            conn.WriteByte(value);
            conn.Flush();
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException(rc);
            }
        }
        finally
        {
            Release();
        }
    }

    public int Peek8(long addr)
    {
        byte[] b = Readmem(addr, 1, new byte[8]);
        return b[0] & 0xFF;
    }

    public void Poke16(long addr, int value)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_POKE16);
            conn.WriteLong(addr);
            conn.WriteShort(value);
            conn.Flush();
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException(rc);
            }
        }
        finally
        {
            Release();
        }
    }

    public int Peek16(long addr)
    {
        byte[] b = Readmem(addr, 2, new byte[8]);
        return BinaryPrimitives.ReadInt16LittleEndian(b) & 0xFFFF;
    }

    public void Poke32(long addr, int value)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_POKE32);
            conn.WriteLong(addr);
            conn.WriteInt(value);
            conn.Flush();
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException(rc);
            }
        }
        finally
        {
            Release();
        }
    }

    public int Peek32(long addr)
    {
        byte[] b = Readmem(addr, 4, new byte[8]);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    public void Poke64(long addr, long value)
    {
        Acquire();
        try
        {
            // The Java client sent COMMAND_POKE32 here, which desynchronised the stream
            // (the sysmodule only consumes 4 value bytes for POKE32).
            conn.WriteCommand(COMMAND_POKE64);
            conn.WriteLong(addr);
            conn.WriteLong(value);
            conn.Flush();
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException(rc);
            }
        }
        finally
        {
            Release();
        }
    }

    public long Peek64(long addr)
    {
        byte[] b = Readmem(addr, 8, new byte[8]);
        return BinaryPrimitives.ReadInt64LittleEndian(b);
    }

    public Result SetWatchpoint(bool read, bool write, long addr)
    {
        // Not wrapped in Acquire()/Release(): SetBreakpoint() acquires the (non-reentrant)
        // semaphore itself, so the Java version deadlocked here.
        WatchpointFlagBuilder.MatchType t;
        if (read & write)
        {
            t = WatchpointFlagBuilder.MatchType.ALL;
        }
        else if (read)
        {
            t = WatchpointFlagBuilder.MatchType.LOAD;
        }
        else if (write)
        {
            t = WatchpointFlagBuilder.MatchType.STORE;
        }
        else
        {
            throw new ArgumentException("No flags set for watchpoint");
        }
        Log.Info("watchpoint " + t);
        int size = 4;
        int id = 0;
        int bpId = id + 4;
        int offset = (int)(addr - (addr & ~3L));
        int mask = ((1 << size) - 1) << offset;

        var bp = new BreakpointFlagBuilder().SetEnabled(true)
            .SetAddressSelect(0xF).SetBreakpointType(BreakpointType.LINKED_CONTEXT_IDR_MATCH);
        var wp = new WatchpointFlagBuilder().SetEnabled(true)
            .SetAccessContol(t).SetAddressSelect(mask).SetLinkedBreakpointNumber(bpId);
        Result rc = SetBreakpoint(bpId, bp.GetFlag(), 0);
        Log.Info("bp:" + rc);
        if (rc.Succeeded)
        {
            rc = SetBreakpoint(0x10 + id, wp.GetFlag(), addr); //wp
            Log.Info("wp:" + rc);
        }
        return rc;
    }

    public Result SetBreakpoint(int id, long flags, long addr)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_SET_BREAKPOINT);
            conn.WriteInt(id);
            conn.WriteLong(addr);
            conn.WriteLong(flags);
            conn.Flush();
            return conn.ReadResult();
        }
        finally
        {
            Release();
        }
    }

    public Result Writemem(byte[] data, long addr)
    {
        return Writemem(data, 0, data.Length, addr);
    }

    public Result Writemem(byte[] data, int off, int len, long addr)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_WRITE);
            conn.WriteLong(addr);
            conn.WriteInt(len);
            conn.Flush();
            Result r = conn.ReadResult();
            if (r.Succeeded)
            {
                conn.Write(data, off, len);
                conn.Flush();
            }
            else
            {
                conn.ReadResult();
                return r;
            }
            return conn.ReadResult();
        }
        finally
        {
            Release();
        }
    }

    public void Readmem(MemoryInfo info, Stream to)
    {
        if (!info.IsReadable)
        {
            return;
        }
        Readmem(info.Address, (int)info.Size, to);
    }

    public void Readmem(long start, int size, Stream to)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_READ);
            conn.WriteLong(start);
            conn.WriteInt(size);
            conn.Flush();

            Result rc = conn.ReadResult();
            if (rc.Succeeded)
            {
                byte[] buffer = new byte[2048 * 4];
                while (size > 0)
                {
                    rc = conn.ReadResult();
                    if (rc.Failed)
                    {
                        conn.ReadResult();
                        throw new ConnectionException(rc);
                    }
                    int len = ReadCompressed(buffer);
                    to.Write(buffer, 0, len);
                    size -= len;
                }
            }
            conn.ReadResult();
        }
        finally
        {
            Release();
        }
    }

    public byte[] Readmem(long addr, int size, byte[]? bytes)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_READ);
            conn.WriteLong(addr);
            conn.WriteInt(size);
            conn.Flush();
            Result rc = conn.ReadResult();

            if (rc.Failed)
            {
                conn.ReadResult(); // ignored
                throw new ConnectionException(rc);
            }

            bytes ??= new byte[size];

            int pos = 0;
            byte[] buffer = new byte[2048 * 4];
            while (pos < size)
            {
                // A failed chunk (e.g. it reaches an unmapped page) is followed only by the final
                // result code. The Java client ignored this rc and parsed the result code as chunk
                // data, which desynchronised the connection.
                rc = conn.ReadResult();
                if (rc.Failed)
                {
                    conn.ReadResult();
                    throw new ConnectionException(rc);
                }
                int len = ReadCompressed(buffer);
                if (len < 0 || len > buffer.Length || pos + len > bytes.Length)
                {
                    throw new ConnectionException("Invalid chunk length " + len);
                }
                Array.Copy(buffer, 0, bytes, pos, len);
                pos += len;
            }
            conn.ReadResult(); // ignored
            return bytes;
        }
        finally
        {
            Release();
        }
    }

    public Result Resume()
    {
        return GetResult(COMMAND_CONTINUE);
    }

    public Result Pause()
    {
        return GetResult(COMMAND_PAUSE);
    }

    public Result Attach(long pid)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_ATTACH);
            conn.WriteLong(pid);
            conn.Flush();
            return conn.ReadResult();
        }
        finally
        {
            Release();
        }
    }

    public Result Detach()
    {
        return GetResult(COMMAND_DETATCH);
    }

    public MemoryInfo Query(long address)
    {
        Acquire();
        try
        {
            if (prev != null && prev.Address != 0 && address >= prev.Address && address < prev.NextAddress)
            {
                return prev;
            }

            conn.WriteCommand(COMMAND_QUERY_MEMORY);
            conn.WriteLong(address);
            conn.Flush();
            return prev = ReadInfo();
        }
        finally
        {
            Release();
        }
    }

    public MemoryInfo[] Query(long start, int max)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_QUERY_MEMORY_MULTI);
            conn.WriteLong(start);
            conn.WriteInt(max);
            conn.Flush();

            var res = new MemoryInfo[max];
            int count;
            for (count = 0; count < max; count++)
            {
                MemoryInfo info = ReadInfo();
                res[count] = info;
                if (info.Type == MemoryType.RESERVED)
                {
                    break;
                }
            }
            conn.ReadResult(); // ignored here, it gets checked in ReadInfo()
            return res[..count];
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// The name of an installed game as the console knows it (from its NACP, in the console's
    /// language). Null if the server can't tell (old sysmodule, not an installed application).
    /// </summary>
    public string? GetTitleName(long titleId)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_GET_TITLE_NAME);
            conn.WriteLong(titleId);
            conn.Flush();
            int len = conn.ReadInt();
            if (len < 0 || len > 0x1000)
            {
                throw new ConnectionException("Invalid title name length " + len);
            }
            byte[] text = new byte[len];
            conn.ReadFully(text);
            Result rc = conn.ReadResult();
            return rc.Failed || len == 0 ? null : System.Text.Encoding.UTF8.GetString(text);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>The sysmodule's recent log lines (kept in its memory, also without an SD card).</summary>
    public string GetLog()
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_GET_LOG);
            conn.Flush();
            int len = conn.ReadInt();
            if (len < 0 || len > 0x100000)
            {
                throw new ConnectionException("Invalid log length " + len);
            }
            byte[] text = new byte[len];
            conn.ReadFully(text);
            conn.ReadResult(); // always 0
            return System.Text.Encoding.UTF8.GetString(text);
        }
        finally
        {
            Release();
        }
    }

    public long GetCurrentPid()
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_CURRENT_PID);
            conn.Flush();
            long pid = conn.ReadLong();
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                pid = 0;
            }
            return pid;
        }
        finally
        {
            Release();
        }
    }

    public long GetAttachedPid()
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_GET_ATTACHED_PID);
            conn.Flush();
            long pid = conn.ReadLong();
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException("This is impossible, so you've done something terribly wrong", rc);
            }
            return pid;
        }
        finally
        {
            Release();
        }
    }

    public long[] GetPids()
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_GET_PIDS);
            conn.Flush();
            int count = conn.ReadInt();
            long[] pids = new long[count];
            for (int i = 0; i < count; i++)
            {
                pids[i] = conn.ReadLong();
            }
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException(rc);
            }
            return pids;
        }
        finally
        {
            Release();
        }
    }

    public long GetTitleId(long pid)
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_GET_TITLEID);
            conn.WriteLong(pid);
            conn.Flush();
            long tid = conn.ReadLong();
            conn.ReadResult(); //TODO throw on failure? idk
            return tid;
        }
        finally
        {
            Release();
        }
    }

    private void Disconnect()
    {
        Acquire();
        try
        {
            conn.WriteCommand(COMMAND_DISCONNECT);
            conn.Flush();
            Result rc = conn.ReadResult();
            if (rc.Failed)
            {
                throw new ConnectionException("This is impossible, so you've done something terribly wrong", rc);
            }
        }
        finally
        {
            Release();
        }
    }

    public long GetCurrentTitleId()
    {
        long pid = GetCurrentPid();
        if (pid == 0)
        {
            return 0;
        }
        return GetTitleId(pid);
    }

    public bool Attached => GetAttachedPid() != 0;

    public bool Connected => conn.Connected;

    /// <summary>Detaches and says goodbye when possible; always releases the connection. Never throws.</summary>
    public void Dispose()
    {
        try
        {
            if (Connected)
            {
                Detach();
                Disconnect();
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "disconnect");
        }
        finally
        {
            try
            {
                conn.Dispose();
            }
            catch (Exception e)
            {
                Log.Error(e, "closing connection");
            }
        }
    }

    private readonly byte[] compressedBuffer = new byte[2048 * 4 * 6];

    private int ReadCompressed(byte[] buffer)
    {
        int compressedFlag = conn.ReadByte();
        int decompressedLen = conn.ReadInt();

        if (compressedFlag == 0)
        {
            conn.ReadFully(buffer, 0, decompressedLen);
        }
        else
        {
            int compressedLen = conn.ReadInt();
            conn.ReadFully(compressedBuffer, 0, compressedLen);
            int pos = 0;
            for (int i = 0; i < compressedLen; i += 2)
            {
                byte value = compressedBuffer[i];
                int count = compressedBuffer[i + 1] & 0xFF;
                Array.Fill(buffer, value, pos, count);
                pos += count;
            }
        }
        return decompressedLen;
    }

    private MemoryInfo ReadInfo()
    {
        long addr = conn.ReadLong();
        long size = conn.ReadLong();
        int type = conn.ReadInt();
        int perm = conn.ReadInt();
        Result rc = conn.ReadResult();
        if (rc.Failed)
        {
            throw new ConnectionException(rc);
        }
        return new MemoryInfo(addr, size, type, perm);
    }

    private Result GetResult(int cmd)
    {
        Acquire();
        try
        {
            conn.WriteCommand(cmd);
            conn.Flush();
            return conn.ReadResult();
        }
        finally
        {
            Release();
        }
    }

    public long Peek(DataType type, long addr)
    {
        return type.Size switch
        {
            1 => Peek8(addr),
            2 => Peek16(addr),
            4 => (uint)Peek32(addr), // zero-extended like 8/16 bit (Java sign-extended 32-bit values)
            8 => Peek64(addr),
            _ => throw new ArgumentException("Illegal data type:" + type),
        };
    }
}
