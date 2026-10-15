using System.Net.Sockets;
using Noexs.Core;

namespace Noexs.IO.Net;

public static class NetworkConstants
{
    /// <summary>noexs 2.x port (0x4E58, "NX"); differs from the original 7331 so old builds never meet.</summary>
    public const int DEFAULT_PORT = 20056;
    public const string DEFAULT_HOST = "192.168.198.1";

    /// <summary>
    /// The Java client blocked forever if the console stopped answering. A generous timeout turns
    /// that into a detectable disconnect instead of a frozen window.
    /// </summary>
    public const int IO_TIMEOUT_MS = 30_000;
}

public sealed class SocketConnection : IConnection
{
    private readonly Socket socket;
    private readonly NetworkStream stream;
    // Separate read/write buffers: a single BufferedStream can't switch direction on a non-seekable stream.
    private readonly BufferedStream input;
    private readonly BufferedStream output;
    private volatile bool closed;

    public SocketConnection(Socket socket)
    {
        this.socket = socket;
        socket.ReceiveTimeout = NetworkConstants.IO_TIMEOUT_MS;
        socket.SendTimeout = NetworkConstants.IO_TIMEOUT_MS;
        // memory dumps stream in continuously; a large window keeps the sysmodule's sends from stalling
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        stream = new NetworkStream(socket, ownsSocket: false);
        input = new BufferedStream(stream, 256 * 1024);
        output = new BufferedStream(stream, 16 * 1024);
    }

    /// <summary>False once the socket was closed locally, by the peer, or broke with an I/O error.</summary>
    public bool Connected => !closed && socket.Connected;

    private ConnectionException Broken(Exception e)
    {
        // after an I/O error the request/response stream is out of sync, so the connection is unusable
        closed = true;
        return new ConnectionException(e);
    }

    private void EnsureOpen()
    {
        if (closed)
        {
            throw new ConnectionException("Connection closed");
        }
    }

    public void WriteByte(int i)
    {
        EnsureOpen();
        try
        {
            output.WriteByte((byte)i);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw Broken(e);
        }
    }

    public void Write(byte[] data, int off, int len)
    {
        EnsureOpen();
        try
        {
            output.Write(data, off, len);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw Broken(e);
        }
    }

    public int ReadByte()
    {
        EnsureOpen();
        try
        {
            int b = input.ReadByte();
            if (b < 0)
            {
                closed = true; // peer closed the connection
            }
            return b;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw Broken(e);
        }
    }

    public int Read(byte[] b, int off, int len)
    {
        EnsureOpen();
        try
        {
            int n = input.Read(b, off, len);
            if (n <= 0 && len > 0)
            {
                closed = true; // peer closed the connection
                return -1;
            }
            return n;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw Broken(e);
        }
    }

    public void Dispose()
    {
        if (closed && !socket.Connected)
        {
            socket.Dispose();
            return;
        }
        try
        {
            if (!closed)
            {
                output.Flush();
            }
        }
        catch (Exception)
        {
            // closing anyway
        }
        finally
        {
            closed = true;
            socket.Dispose();
        }
    }

    public void Flush()
    {
        EnsureOpen();
        try
        {
            output.Flush();
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw Broken(e);
        }
    }
}
