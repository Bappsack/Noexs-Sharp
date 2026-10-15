using System.Runtime.InteropServices;
using Noexs.Core;

namespace Noexs.IO.Usb;

/// <summary>A device on the bus (javax.usb UsbDevice equivalent).</summary>
public class UsbDevice
{
    internal UsbDevice(IntPtr handle, LibUsb.DeviceDescriptor descriptor)
    {
        Handle = handle;
        Descriptor = descriptor;
    }

    internal IntPtr Handle { get; }

    internal LibUsb.DeviceDescriptor Descriptor { get; }

    public ushort VendorId => Descriptor.idVendor;

    public ushort ProductId => Descriptor.idProduct;

    public virtual bool IsUsbHub => Descriptor.bDeviceClass == LibUsb.LIBUSB_CLASS_HUB;

    public override string ToString()
    {
        return $"{(IsUsbHub ? "Hub" : "Device")} {VendorId:x4}:{ProductId:x4}";
    }
}

/// <summary>A hub with attached devices. The root hub is virtual, like in javax.usb.</summary>
public sealed class UsbHub : UsbDevice
{
    private readonly bool virtualRoot;

    internal UsbHub(IntPtr handle, LibUsb.DeviceDescriptor descriptor, bool virtualRoot = false)
        : base(handle, descriptor)
    {
        this.virtualRoot = virtualRoot;
    }

    public List<UsbDevice> AttachedUsbDevices { get; } = new();

    public override bool IsUsbHub => true;

    public override string ToString()
    {
        return virtualRoot ? "Virtual Root Hub" : base.ToString();
    }
}

public static class UsbUtils
{
    private const int SWITCH_VENDOR_ID = 0x57E;
    private const int SWITCH_PROD_ID = 0x3000;

    public static UsbDevice? FindSwitch()
    {
        return FindDevice(SWITCH_VENDOR_ID, SWITCH_PROD_ID);
    }

    /// <summary>
    /// Opens a connection to the noexs sysmodule over USB: on Windows through the libusbK driver
    /// (libusbK.dll), elsewhere through libusb.
    /// </summary>
    public static IConnection OpenSwitch()
    {
        if (OperatingSystem.IsWindows())
        {
            return LibUsbKConnection.Open(SWITCH_VENDOR_ID, SWITCH_PROD_ID);
        }
        UsbDevice device = FindSwitch()
                           ?? throw new UsbException("Switch not found over USB. Is noexs running and the cable connected?");
        return UsbConnection.Open(device);
    }

    public static UsbDevice? FindDevice(int vendorId, int productId)
    {
        return FindDevice(GetRootHub(), (ushort)vendorId, (ushort)productId);
    }

    //Adapted from usb4java examples
    public static UsbDevice? FindDevice(UsbHub hub, ushort vendorId, ushort productId)
    {
        foreach (UsbDevice device in hub.AttachedUsbDevices)
        {
            if (device.VendorId == vendorId && device.ProductId == productId) return device;
            if (device is UsbHub h)
            {
                UsbDevice? d = FindDevice(h, vendorId, productId);
                if (d != null) return d;
            }
        }
        return null;
    }

    /// <summary>
    /// Device tree for the Utils tab: libusb when available, otherwise (Windows without
    /// libusb-1.0.dll) a flat list from SetupAPI.
    /// </summary>
    public static UsbHub GetRootHub()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return GetRootHubLibUsb();
            }
            catch (DllNotFoundException)
            {
                var root = new UsbHub(IntPtr.Zero, default, virtualRoot: true);
                foreach (var (_, vid, pid) in UsbDeviceList.EnumerateDevices().DistinctBy(d => (d.Vid, d.Pid, d.Path)))
                {
                    root.AttachedUsbDevices.Add(new UsbDevice(IntPtr.Zero,
                        new LibUsb.DeviceDescriptor { idVendor = vid, idProduct = pid }));
                }
                return root;
            }
        }
        return GetRootHubLibUsb();
    }

    /// <summary>Enumerates the bus and builds the device tree from libusb parent links.</summary>
    private static UsbHub GetRootHubLibUsb()
    {
        IntPtr ctx = LibUsb.Context;
        nint count = LibUsb.GetDeviceList(ctx, out IntPtr list);
        if (count < 0)
        {
            LibUsb.Check((int)count, "libusb_get_device_list");
        }

        var root = new UsbHub(IntPtr.Zero, default, virtualRoot: true);
        var byHandle = new Dictionary<IntPtr, UsbDevice>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr dev = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                if (LibUsb.GetDeviceDescriptor(dev, out var desc) < 0)
                {
                    continue;
                }
                // keep a reference; the list itself is released below
                LibUsb.RefDevice(dev);
                byHandle[dev] = desc.bDeviceClass == LibUsb.LIBUSB_CLASS_HUB
                    ? new UsbHub(dev, desc)
                    : new UsbDevice(dev, desc);
            }

            foreach (var (handle, device) in byHandle)
            {
                IntPtr parent = LibUsb.GetParent(handle);
                if (parent != IntPtr.Zero && byHandle.TryGetValue(parent, out var p) && p is UsbHub parentHub)
                {
                    parentHub.AttachedUsbDevices.Add(device);
                }
                else
                {
                    root.AttachedUsbDevices.Add(device);
                }
            }
        }
        finally
        {
            LibUsb.FreeDeviceList(list, 1);
        }
        return root;
    }

    public static bool IsSwitch(UsbDevice d)
    {
        return d.ProductId == SWITCH_PROD_ID && d.VendorId == SWITCH_VENDOR_ID;
    }
}
