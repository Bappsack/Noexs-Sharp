using Noexs.Core;
using Noexs.Util;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Noexs.IO.Usb;

namespace Noexs.UI.Views;

public partial class UtilsTab : UserControl, IController
{
    private MainWindow mc = null!;

    /// <summary>The compression box's items, in order.</summary>
    private static readonly DumpCompression[] CompressionChoices = [DumpCompression.Lz4, DumpCompression.Rle, DumpCompression.Off];

    public UtilsTab()
    {
        InitializeComponent();
        compressionBox.SelectedIndex = Array.IndexOf(CompressionChoices, Settings.GetDumpCompression());
        doubleBufferBox.IsChecked = Settings.GetDoubleBuffering();
        compressionBox.SelectionChanged += (_, _) => OnTransferOptionChanged();
        doubleBufferBox.IsCheckedChanged += (_, _) => OnTransferOptionChanged();
        deviceTree.SelectionChanged += (_, _) =>
        {
            if (deviceTree.SelectedItem is TreeViewItem { Tag: UsbDevice d })
            {
                UpdateInfo(d);
            }
        };
    }

    private void UpdateInfo(UsbDevice? d)
    {
        if (d == null)
        {
            infoLabel.Text = "";
            return;
        }
        var sb = new StringBuilder("[");
        sb.Append(d.IsUsbHub ? "HUB" : "DEVICE");
        sb.Append("] ");
        if (d is UsbHub hub)
        {
            // libusb can't open hubs on every platform, so the hub's port count isn't reported
            sb.Append("Attached Devices: ").Append(hub.AttachedUsbDevices.Count);
        }
        else
        {
            sb.Append("Device Id:").Append(d.VendorId.ToString("x4"))
                .Append(':').Append(d.ProductId.ToString("x4"));
            sb.Append(" Is Switch:").Append(UsbUtils.IsSwitch(d) ? "true" : "false");
        }
        infoLabel.Text = sb.ToString();
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
    }

    public void OnConnect()
    {
        logPane.IsEnabled = true;
    }

    public void OnDisconnect()
    {
        logPane.IsEnabled = false;
    }

    private void OnTransferOptionChanged()
    {
        if (compressionBox.SelectedIndex >= 0)
        {
            Settings.SetDumpCompression(CompressionChoices[compressionBox.SelectedIndex]);
        }
        Settings.SetDoubleBuffering(doubleBufferBox.IsChecked == true);
        mc.ApplyTransferOptions();
    }

    private void OnRefreshLog(object? sender, RoutedEventArgs e)
    {
        if (!mc.RequireConnection())
        {
            return;
        }
        Noexs.Core.Debugger debugger = mc.GetDebugger();
        Task.Run(() =>
        {
            string text;
            try
            {
                text = debugger.GetLog();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "switch log");
                text = "Unable to read the log: " + ex.Message;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                logText.Text = text.Replace("\r\n", "\n");
                logText.CaretIndex = logText.Text.Length; // newest at the bottom
            });
        });
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && !string.IsNullOrEmpty(logText.Text))
            {
                await clipboard.SetTextAsync(logText.Text);
                mc.SetStatus("Copied the Switch log");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "clipboard");
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        UpdateInfo(null);
        UsbHub hub;
        try
        {
            hub = UsbUtils.GetRootHub();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            MessageDialog.ShowMessage("Unable to enumerate USB devices (is libusb-1.0 available?)\n\n" + ex.Message,
                AlertType.ERROR);
            return;
        }
        var root = new TreeViewItem { Header = hub.ToString(), Tag = hub };
        ScanHub(root, hub);
        deviceTree.Items.Clear();
        deviceTree.Items.Add(root);
    }

    private static void ScanHub(TreeViewItem root, UsbHub hub)
    {
        root.IsExpanded = true;
        foreach (UsbDevice d in hub.AttachedUsbDevices)
        {
            var node = new TreeViewItem { Header = d.ToString(), Tag = d };
            if (d is UsbHub h)
            {
                ScanHub(node, h);
            }
            root.Items.Add(node);
        }
    }
}
