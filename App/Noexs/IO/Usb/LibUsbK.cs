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

    private IntPtr handle;
    private readonly byte pipeIn;
    private readonly byte pipeOut;

    private LibUsbKConnection(IntPtr handle, byte pipeIn, byte pipeOut)
    {
        this.handle = handle;
        this.pipeIn = pipeIn;
        this.pipeOut = pipeOut;
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
                LibUsbK.SetTimeout(handle, pipeIn, TimeoutMs);
                LibUsbK.SetTimeout(handle, pipeOut, TimeoutMs);
                return new LibUsbKConnection(handle, pipeIn, pipeOut);
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

    protected override unsafe int TransferIn(byte[] buf, int off, int len)
    {
        fixed (byte* p = buf)
        {
            if (!LibUsbK.UsbK_ReadPipe(handle, pipeIn, p + off, (uint)len, out uint n, IntPtr.Zero))
            {
                throw new UsbException("USB read failed (error " + Marshal.GetLastPInvokeError() + ")");
            }
            return (int)n;
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
        if (handle != IntPtr.Zero)
        {
            LibUsbK.UsbK_Free(handle);
            handle = IntPtr.Zero;
        }
    }
}
