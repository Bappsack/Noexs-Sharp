using System.Runtime.InteropServices;

namespace Noexs.IO.Usb;

/// <summary>
/// Minimal libusb-1.0 binding (replaces usb4java/javax.usb). Requires libusb-1.0.dll / .so / .dylib
/// to be resolvable at runtime, e.g. next to the executable.
/// </summary>
internal static partial class LibUsb
{
    private const string Lib = "libusb-1.0";

    public const byte LIBUSB_CLASS_HUB = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ConfigDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort wTotalLength;
        public byte bNumInterfaces;
        public byte bConfigurationValue;
        public byte iConfiguration;
        public byte bmAttributes;
        public byte MaxPower;
        public IntPtr interfaces; // const libusb_interface*
        public IntPtr extra;
        public int extra_length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Interface
    {
        public IntPtr altsetting; // const libusb_interface_descriptor*
        public int num_altsetting;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InterfaceDescriptor
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
        public IntPtr endpoint; // const libusb_endpoint_descriptor*
        public IntPtr extra;
        public int extra_length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EndpointDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bEndpointAddress;
        public byte bmAttributes;
        public ushort wMaxPacketSize;
        public byte bInterval;
        public byte bRefresh;
        public byte bSynchAddress;
        public IntPtr extra;
        public int extra_length;
    }

    [LibraryImport(Lib, EntryPoint = "libusb_get_active_config_descriptor")]
    public static partial int GetActiveConfigDescriptor(IntPtr dev, out IntPtr config);

    [LibraryImport(Lib, EntryPoint = "libusb_free_config_descriptor")]
    public static partial void FreeConfigDescriptor(IntPtr config);

    [LibraryImport(Lib, EntryPoint = "libusb_set_auto_detach_kernel_driver")]
    public static partial int SetAutoDetachKernelDriver(IntPtr handle, int enable);

    [LibraryImport(Lib, EntryPoint = "libusb_init")]
    public static partial int Init(out IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "libusb_get_device_list")]
    public static partial nint GetDeviceList(IntPtr ctx, out IntPtr list);

    [LibraryImport(Lib, EntryPoint = "libusb_free_device_list")]
    public static partial void FreeDeviceList(IntPtr list, int unrefDevices);

    [LibraryImport(Lib, EntryPoint = "libusb_ref_device")]
    public static partial IntPtr RefDevice(IntPtr dev);

    [LibraryImport(Lib, EntryPoint = "libusb_get_device_descriptor")]
    public static partial int GetDeviceDescriptor(IntPtr dev, out DeviceDescriptor desc);

    [LibraryImport(Lib, EntryPoint = "libusb_get_parent")]
    public static partial IntPtr GetParent(IntPtr dev);

    [LibraryImport(Lib, EntryPoint = "libusb_open")]
    public static partial int Open(IntPtr dev, out IntPtr handle);

    [LibraryImport(Lib, EntryPoint = "libusb_close")]
    public static partial void Close(IntPtr handle);

    [LibraryImport(Lib, EntryPoint = "libusb_claim_interface")]
    public static partial int ClaimInterface(IntPtr handle, int interfaceNumber);

    [LibraryImport(Lib, EntryPoint = "libusb_release_interface")]
    public static partial int ReleaseInterface(IntPtr handle, int interfaceNumber);

    [LibraryImport(Lib, EntryPoint = "libusb_bulk_transfer")]
    public static unsafe partial int BulkTransfer(IntPtr handle, byte endpoint, byte* data, int length,
        out int transferred, uint timeout);

    [LibraryImport(Lib, EntryPoint = "libusb_error_name")]
    private static partial IntPtr ErrorNamePtr(int errorCode);

    public static string ErrorName(int errorCode)
    {
        return Marshal.PtrToStringAnsi(ErrorNamePtr(errorCode)) ?? errorCode.ToString();
    }

    private static readonly Lazy<IntPtr> context = new(() =>
    {
        int rc = Init(out IntPtr ctx);
        if (rc != 0)
        {
            throw new UsbException("libusb_init failed: " + ErrorName(rc));
        }
        return ctx;
    });

    public static IntPtr Context => context.Value;

    public static void Check(int rc, string what)
    {
        if (rc < 0)
        {
            throw new UsbException(what + " failed: " + ErrorName(rc));
        }
    }
}

public class UsbException : Exception
{
    public UsbException(string message) : base(message)
    {
    }
}
