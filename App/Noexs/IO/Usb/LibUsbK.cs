using System.Runtime.InteropServices;
using Noexs.Core;

namespace Noexs.IO.Usb;

/// <summary>
/// The libusbK "K" API (libusbK.dll), the only USB backend on Windows. Zadig installs the DLL into
/// System32 together with the libusbK driver (the driver DBI, NS-USBloader and Goldleaf use too).
/// </summary>
internal static partial class LibUsbK
{
    private const string Dll = "libusbK.dll";

    // KLST_DEVINFO starts with: INT Vid; INT Pid; INT MI; CHAR InstanceID[256]; INT DriverID; ...
    private const int DevInfoDriverIdOffset = 12 + 256;
    private const int DriverIdLibUsbK = 0;

    private const uint PIPE_TRANSFER_TIMEOUT = 0x03;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    /// <summary>WINUSB_PIPE_INFORMATION, which libusbK uses as its pipe information too.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PIPE_INFORMATION
    {
        public int PipeType; // 2 = bulk
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LstK_Init(out IntPtr deviceList, int flags);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LstK_Free(IntPtr deviceList);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LstK_FindByVidPid(IntPtr deviceList, int vid, int pid, out IntPtr deviceInfo);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UsbK_Init(out IntPtr handle, IntPtr deviceInfo);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UsbK_Free(IntPtr handle);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UsbK_QueryInterfaceSettings(IntPtr handle, byte altSettingIndex, out USB_INTERFACE_DESCRIPTOR descriptor);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UsbK_QueryPipe(IntPtr handle, byte altSettingNumber, byte pipeIndex, out PIPE_INFORMATION pipeInformation);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UsbK_SetPipePolicy(IntPtr handle, byte pipeId, uint policyType, uint valueLength, ref uint value);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool UsbK_ReadPipe(IntPtr handle, byte pipeId, byte* buffer, uint length,
        out uint transferred, IntPtr overlapped);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool UsbK_WritePipe(IntPtr handle, byte pipeId, byte* buffer, uint length,
        out uint transferred, IntPtr overlapped);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UsbK_AbortPipe(IntPtr handle, byte pipeId);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UsbK_GetOverlappedResult(IntPtr handle, IntPtr overlapped, out uint transferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr CreateEventW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ResetEvent(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(IntPtr handle, uint ms);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    public const int ERROR_IO_PENDING = 997;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    /// <summary>True when libusbK.dll can be loaded (i.e. the libusbK driver was installed with Zadig).</summary>
    public static bool Available
    {
        get
        {
            try
            {
                if (!NativeLibrary.TryLoad(Dll, typeof(LibUsbK).Assembly, null, out IntPtr h))
                {
                    return false;
                }
                NativeLibrary.Free(h);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static int DriverId(IntPtr deviceInfo)
    {
        return Marshal.ReadInt32(deviceInfo, DevInfoDriverIdOffset);
    }

    public static string DriverName(int driverId)
    {
        return driverId switch
        {
            DriverIdLibUsbK => "libusbK",
            1 => "libusb0",
            2 => "WinUSB",
            3 => "libusb0 filter",
            int other => "driver " + other,
        };
    }

    public static bool IsLibUsbK(int driverId) => driverId == DriverIdLibUsbK;

    public static void SetTimeout(IntPtr handle, byte pipeId, uint ms)
    {
        UsbK_SetPipePolicy(handle, pipeId, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref ms);
    }
}

/// <summary>Connection to the noexs sysmodule over USB on Windows (libusbK driver + libusbK.dll).</summary>
public sealed class LibUsbKConnection : BulkConnection
{
    private const string ZadigSteps =
        "Install the libusbK driver once with Zadig (https://zadig.akeo.ie/): Options → List All Devices, " +
        "select the device with USB ID 057E 3000, choose \"libusbK\" and click Install / Replace Driver.";

    // Read-ahead: several reads are always queued on the IN pipe, so the host controller takes the
    // next packets while the App is still decoding the previous block. With one read at a time the
    // link idles between reads (the Switch has to wait until the PC asks again).
    private const int ReadAheadSlots = 4;
    private const int SlotSize = ReadTransferSize;

    private unsafe struct Slot
    {
        public byte* Buffer;
        public IntPtr Overlapped; // OVERLAPPED, its hEvent is Event
        public IntPtr Event;
        public bool Pending;
        public int Length;
        public int Pos;
    }

    private IntPtr handle;
    private readonly byte pipeIn;
    private readonly byte pipeOut;
    private readonly Slot[] slots = new Slot[ReadAheadSlots];
    private int head;
    private int readTimeoutMs = TimeoutMs;
    private readonly object io = new();

    // OVERLAPPED: Internal, InternalHigh (ULONG_PTR), Offset + OffsetHigh (2x DWORD), hEvent
    private static int OverlappedEventOffset => 2 * IntPtr.Size + 8;

    private LibUsbKConnection(IntPtr handle, byte pipeIn, byte pipeOut)
    {
        this.handle = handle;
        this.pipeIn = pipeIn;
        this.pipeOut = pipeOut;
    }

    /// <summary>Allocates the read-ahead buffers and queues a read on each of them.</summary>
    private unsafe void StartReadAhead()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ref Slot s = ref slots[i];
            s.Buffer = (byte*)NativeMemory.AlignedAlloc(SlotSize, 0x1000);
            s.Overlapped = (IntPtr)NativeMemory.AllocZeroed((nuint)(OverlappedEventOffset + IntPtr.Size));
            s.Event = LibUsbK.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            if (s.Event == IntPtr.Zero)
            {
                throw new UsbException("Unable to create a USB read event (error " + Marshal.GetLastPInvokeError() + ")");
            }
            Marshal.WriteIntPtr(s.Overlapped, OverlappedEventOffset, s.Event);
        }
        for (int i = 0; i < slots.Length; i++)
        {
            Submit(ref slots[i]);
        }
    }

    /// <summary>Queues a read into the slot's buffer.</summary>
    private unsafe void Submit(ref Slot s)
    {
        s.Length = s.Pos = 0;
        LibUsbK.ResetEvent(s.Event);
        new Span<byte>((void*)s.Overlapped, OverlappedEventOffset).Clear(); // keeps hEvent
        if (!LibUsbK.UsbK_ReadPipe(handle, pipeIn, s.Buffer, SlotSize, out _, s.Overlapped))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != LibUsbK.ERROR_IO_PENDING)
            {
                throw new UsbException("USB read failed (error " + error + ")");
            }
        }
        s.Pending = true;
    }

    /// <summary>Opens the Switch (vid:pid); throws a <see cref="UsbException"/> that says what to fix.</summary>
    public static LibUsbKConnection Open(ushort vid, ushort pid)
    {
        if (!LibUsbK.Available)
        {
            throw new UsbException("libusbK.dll was not found, so the libusbK driver isn't installed.\n\n" + ZadigSteps);
        }
        if (!LibUsbK.LstK_Init(out IntPtr list, 0))
        {
            throw new UsbException("libusbK could not list the USB devices (error " + Marshal.GetLastPInvokeError() + ")");
        }
        try
        {
            if (!LibUsbK.LstK_FindByVidPid(list, vid, pid, out IntPtr info) || info == IntPtr.Zero)
            {
                throw NotFound(vid, pid);
            }
            int driver = LibUsbK.DriverId(info);
            if (!LibUsbK.IsLibUsbK(driver))
            {
                throw new UsbException($"The Switch ({vid:X4}:{pid:X4}) uses the {LibUsbK.DriverName(driver)} driver, " +
                                       "but noexs needs libusbK.\n\n" + ZadigSteps);
            }
            if (!LibUsbK.UsbK_Init(out IntPtr handle, info))
            {
                throw new UsbException($"The Switch was found but could not be opened (error {Marshal.GetLastPInvokeError()}). " +
                                       "Is another program (NS-USBloader, DBI's PC side, ...) using it?");
            }
            try
            {
                if (!LibUsbK.UsbK_QueryInterfaceSettings(handle, 0, out var iface))
                {
                    throw new UsbException("Unable to read the USB interface (error " + Marshal.GetLastPInvokeError() + ")");
                }
                byte pipeIn = 0, pipeOut = 0;
                for (byte i = 0; i < iface.bNumEndpoints; i++)
                {
                    if (!LibUsbK.UsbK_QueryPipe(handle, 0, i, out var pipe) || pipe.PipeType != 2)
                    {
                        continue;
                    }
                    if ((pipe.PipeId & 0x80) != 0)
                    {
                        pipeIn = pipeIn == 0 ? pipe.PipeId : pipeIn;
                    }
                    else
                    {
                        pipeOut = pipeOut == 0 ? pipe.PipeId : pipeOut;
                    }
                }
                if (pipeIn == 0 || pipeOut == 0)
                {
                    throw new UsbException("The USB device has no bulk IN/OUT endpoints");
                }
                DiscardStaleData(handle, pipeIn);
                // queued reads wait as long as the App is idle; the read timeout applies to
                // waiting for one of them instead
                LibUsbK.SetTimeout(handle, pipeIn, 0);
                LibUsbK.SetTimeout(handle, pipeOut, TimeoutMs);
                var connection = new LibUsbKConnection(handle, pipeIn, pipeOut);
                try
                {
                    connection.StartReadAhead();
                }
                catch
                {
                    connection.FreeReadAhead();
                    throw;
                }
                return connection;
            }
            catch
            {
                LibUsbK.UsbK_Free(handle);
                throw;
            }
        }
        finally
        {
            LibUsbK.LstK_Free(list);
        }
    }

    /// <summary>
    /// USB has no connection that closes: if an earlier session ended while the sysmodule was still
    /// sending (cable pulled, App closed during a dump, a request timed out), that data is still
    /// pending and the sysmodule waits until the PC reads it. Read and discard it, so the sysmodule
    /// is free again and the next reply isn't mixed up with old bytes.
    /// </summary>
    private static unsafe void DiscardStaleData(IntPtr handle, byte pipeIn)
    {
        LibUsbK.SetTimeout(handle, pipeIn, 250);
        byte[] buffer = new byte[ReadTransferSize];
        long discarded = 0;
        fixed (byte* p = buffer)
        {
            // at most ~256 MiB (a whole interrupted dump chunk is far less)
            for (int i = 0; i < 4096 && LibUsbK.UsbK_ReadPipe(handle, pipeIn, p, (uint)buffer.Length, out uint n, IntPtr.Zero) && n > 0; i++)
            {
                discarded += n;
            }
        }
        if (discarded > 0)
        {
            Util.Log.Info($"usb: discarded {discarded} bytes left over from an earlier session");
        }
    }

    /// <summary>libusbK doesn't see the device: find out whether it's absent or has no/another driver.</summary>
    private static UsbException NotFound(ushort vid, ushort pid)
    {
        var devices = UsbDeviceList.EnumerateDevices();
        if (devices.Any(d => d.Vid == vid && d.Pid == pid))
        {
            return new UsbException($"The Switch ({vid:X4}:{pid:X4}) is connected but has no libusbK driver.\n\n" + ZadigSteps);
        }
        var other = devices.Where(d => d.Vid == vid).Select(d => $"{d.Vid:X4}:{d.Pid:X4}").Distinct().ToList();
        if (other.Count > 0)
        {
            return new UsbException(
                $"The Switch is connected, but as {string.Join(", ", other)} instead of the noexs USB device {vid:X4}:{pid:X4}.\n\n" +
                "noexs' USB mode isn't active on the console. Check that:\n" +
                "  - the noexs sysmodule is installed: atmosphere/contents/4200000000004E58/exefs.nsp\n" +
                "    (with flags/boot2.flag) and the console was rebooted\n" +
                "  - sdmc:/config/noexs/usb_disabled.flag does not exist\n" +
                "  - no other homebrew is using USB\n\n" +
                "Utils → Switch Log (over Wi-Fi) shows what noexs did (\"usb: ready\" means it is waiting for the PC).");
        }
        return new UsbException($"No USB device {vid:X4}:{pid:X4} found. Is the Switch connected (USB-C to the PC, not through " +
                                "the dock) and noexs running?");
    }

    protected override bool IsOpen => handle != IntPtr.Zero;

    protected override void SetInTimeout(int ms)
    {
        readTimeoutMs = ms;
    }

    protected override unsafe int TransferIn(byte[] buf, int off, int len)
    {
        lock (io)
        {
            while (true)
            {
                if (handle == IntPtr.Zero)
                {
                    throw new UsbException("USB connection closed");
                }
                ref Slot s = ref slots[head];
                if (s.Pending)
                {
                    uint wait = LibUsbK.WaitForSingleObject(s.Event, (uint)readTimeoutMs);
                    if (wait == LibUsbK.WAIT_TIMEOUT)
                    {
                        throw new UsbException($"USB read timed out (no data from the Switch for {readTimeoutMs} ms)");
                    }
                    s.Pending = false;
                    if (wait != LibUsbK.WAIT_OBJECT_0 || !LibUsbK.UsbK_GetOverlappedResult(handle, s.Overlapped, out uint n, false))
                    {
                        throw new UsbException("USB read failed (error " + Marshal.GetLastPInvokeError() + ")");
                    }
                    s.Length = (int)n;
                    s.Pos = 0;
                }
                int count = Math.Min(len, s.Length - s.Pos);
                if (count > 0)
                {
                    new ReadOnlySpan<byte>(s.Buffer + s.Pos, count).CopyTo(buf.AsSpan(off, count));
                    s.Pos += count;
                }
                if (s.Pos == s.Length)
                {
                    // used up (or a zero-length packet): queue it again behind the others
                    head = (head + 1) % slots.Length;
                    Submit(ref s);
                }
                if (count > 0)
                {
                    return count;
                }
            }
        }
    }

    /// <summary>Cancels the queued reads and frees their buffers.</summary>
    private unsafe void FreeReadAhead()
    {
        if (handle != IntPtr.Zero)
        {
            LibUsbK.UsbK_AbortPipe(handle, pipeIn);
        }
        for (int i = 0; i < slots.Length; i++)
        {
            ref Slot s = ref slots[i];
            if (s.Pending && s.Event != IntPtr.Zero)
            {
                // the driver may write into the buffer until the aborted read has completed
                LibUsbK.WaitForSingleObject(s.Event, 2000);
                s.Pending = false;
            }
            if (s.Event != IntPtr.Zero)
            {
                LibUsbK.CloseHandle(s.Event);
                s.Event = IntPtr.Zero;
            }
            if (s.Overlapped != IntPtr.Zero)
            {
                NativeMemory.Free((void*)s.Overlapped);
                s.Overlapped = IntPtr.Zero;
            }
            if (s.Buffer != null)
            {
                NativeMemory.AlignedFree(s.Buffer);
                s.Buffer = null;
            }
        }
    }

    protected override unsafe void TransferOut(byte[] buf, int off, int len)
    {
        fixed (byte* p = buf)
        {
            if (!LibUsbK.UsbK_WritePipe(handle, pipeOut, p + off, (uint)len, out uint n, IntPtr.Zero) || n != len)
            {
                throw new UsbException("USB write failed (error " + Marshal.GetLastPInvokeError() + ")");
            }
        }
    }

    protected override void Close()
    {
        IntPtr h = handle;
        if (h == IntPtr.Zero)
        {
            return;
        }
        // a read waiting on another thread completes with an error and releases the lock
        LibUsbK.UsbK_AbortPipe(h, pipeIn);
        lock (io)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }
            FreeReadAhead();
            LibUsbK.UsbK_Free(handle);
            handle = IntPtr.Zero;
        }
    }
}
