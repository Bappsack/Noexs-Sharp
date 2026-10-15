using System.Runtime.InteropServices;

namespace Noexs.IO.Usb;

/// <summary>
/// Connection to the noexs sysmodule over libusb (Linux/macOS, or Windows with libusb-1.0.dll).
/// The vendor-specific interface and its bulk endpoints are found from the configuration
/// descriptor instead of being hard-coded.
/// </summary>
public sealed class UsbConnection : BulkConnection
{
    private IntPtr handle;
    private readonly int interfaceNumber;
    private readonly byte endpointIn;
    private readonly byte endpointOut;

    private UsbConnection(IntPtr handle, int interfaceNumber, byte endpointIn, byte endpointOut)
    {
        this.handle = handle;
        this.interfaceNumber = interfaceNumber;
        this.endpointIn = endpointIn;
        this.endpointOut = endpointOut;
    }

    public static UsbConnection Open(UsbDevice device)
    {
        (int iface, byte epIn, byte epOut) = FindBulkInterface(device.Handle);
        LibUsb.Check(LibUsb.Open(device.Handle, out IntPtr handle), "libusb_open");
        try
        {
            LibUsb.SetAutoDetachKernelDriver(handle, 1); // not supported everywhere; harmless
            LibUsb.Check(LibUsb.ClaimInterface(handle, iface), "libusb_claim_interface");
        }
        catch
        {
            LibUsb.Close(handle);
            throw;
        }
        return new UsbConnection(handle, iface, epIn, epOut);
    }

    /// <summary>First interface with a bulk IN and a bulk OUT endpoint (noexs uses a vendor class one).</summary>
    private static unsafe (int Interface, byte In, byte Out) FindBulkInterface(IntPtr dev)
    {
        LibUsb.Check(LibUsb.GetActiveConfigDescriptor(dev, out IntPtr configPtr), "libusb_get_active_config_descriptor");
        try
        {
            var config = *(LibUsb.ConfigDescriptor*)configPtr;
            var interfaces = (LibUsb.Interface*)config.interfaces;
            for (int i = 0; i < config.bNumInterfaces; i++)
            {
                var alts = (LibUsb.InterfaceDescriptor*)interfaces[i].altsetting;
                for (int a = 0; a < interfaces[i].num_altsetting; a++)
                {
                    LibUsb.InterfaceDescriptor desc = alts[a];
                    var endpoints = (LibUsb.EndpointDescriptor*)desc.endpoint;
                    byte epIn = 0, epOut = 0;
                    for (int e = 0; e < desc.bNumEndpoints; e++)
                    {
                        LibUsb.EndpointDescriptor ep = endpoints[e];
                        if ((ep.bmAttributes & 0x3) != 2) // bulk
                        {
                            continue;
                        }
                        if ((ep.bEndpointAddress & 0x80) != 0)
                        {
                            epIn = epIn == 0 ? ep.bEndpointAddress : epIn;
                        }
                        else
                        {
                            epOut = epOut == 0 ? ep.bEndpointAddress : epOut;
                        }
                    }
                    if (epIn != 0 && epOut != 0)
                    {
                        return (desc.bInterfaceNumber, epIn, epOut);
                    }
                }
            }
        }
        finally
        {
            LibUsb.FreeConfigDescriptor(configPtr);
        }
        throw new UsbException("The USB device has no bulk IN/OUT interface");
    }

    protected override bool IsOpen => handle != IntPtr.Zero;

    protected override unsafe int TransferIn(byte[] buf, int off, int len)
    {
        fixed (byte* p = buf)
        {
            int rc = LibUsb.BulkTransfer(handle, endpointIn, p + off, len, out int transferred, TimeoutMs);
            if (rc < 0)
            {
                throw new UsbException("libusb_bulk_transfer (in) failed: " + LibUsb.ErrorName(rc));
            }
            return transferred;
        }
    }

    protected override unsafe void TransferOut(byte[] buf, int off, int len)
    {
        fixed (byte* p = buf)
        {
            int rc = LibUsb.BulkTransfer(handle, endpointOut, p + off, len, out int transferred, TimeoutMs);
            if (rc < 0 || transferred != len)
            {
                throw new UsbException("libusb_bulk_transfer (out) failed: " + LibUsb.ErrorName(rc));
            }
        }
    }

    protected override void Close()
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        LibUsb.ReleaseInterface(handle, interfaceNumber);
        LibUsb.Close(handle);
        handle = IntPtr.Zero;
    }
}
