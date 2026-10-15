using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Noexs.Core;
using Noexs.IO.Net;
using Noexs.Misc;
using Noexs.UI.Models;
using Noexs.UI.Services;
using Noexs.Util;

namespace Noexs.UI.Views;

public interface IController
{
    void SetMainController(MainWindow c);

    void OnConnect();

    void OnDisconnect();

    void Stop()
    {
    }
}

/// <summary>Main window + MainController.</summary>
public partial class MainWindow : Window, IController
{
    public enum Tab
    {
        TOOLS,
        SEARCH,
        POINTER_SEARCH,
        MEMORY_VIEWER,
        WATCH_LIST,
        DISASSEMBLER,
        SIGNATURES,
        DUMP_PARSER,
        UTILS,
    }

    public static MainWindow? Instance { get; private set; }

    private volatile Debugger debugger = new(NopConnection.INSTANCE);

    private readonly List<IController> controllers = new();
    private readonly DebuggerConnectionService connectionService = new();
    private readonly CancellationTokenSource timerCts = new();
    private readonly DispatcherTimer connectionWatchdog;
    private volatile bool autoResumeChecked;
    private string chooserDir = Settings.GetChooserDir();
    private BackgroundService? boundProgress;
    private bool sessionActive;
    private bool stopped;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        // notices a dropped connection (console rebooted, Wi-Fi lost, timeout) and resets the UI
        connectionWatchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) =>
            {
                if (sessionActive && !debugger.Connected)
                {
                    HandleConnectionLost();
                }
            });

        Initialize();
        Closed += (_, _) => Stop();
    }

    private void Initialize()
    {
        controllers.Add(this);
        controllers.Add(toolsTabPage);
        controllers.Add(memViewTabPage);
        controllers.Add(searchTabPage);
        controllers.Add(disassembleTabPage);
        controllers.Add(pointerTabPage);
        controllers.Add(watchlistTabPage);
        controllers.Add(signatureTabPage);
        controllers.Add(dumpParserTabPage);
        controllers.Add(utilsTabPage);

        Fire(c => c.SetMainController(this));
        Fire(c => c.OnDisconnect());

        SetProgress(0);
        connectionService.MessageChanged += SetStatus;

        ipAddr.Text = Settings.GetConnectionHost();
        // the history button is only usable together with the address box (network connections)
        ipAddr.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsEnabledProperty)
            {
                ipHistoryBtn.IsEnabled = ipAddr.IsEnabled;
            }
        };

        connectionType.ItemsSource = new[] { ConnectionType.USB, ConnectionType.NETWORK };
        connectionType.SelectedItem = ConnectionType.NETWORK; //TODO save/store this
        connectionType.SelectionChanged += (_, _) =>
            ipAddr.IsEnabled = connectionType.SelectedItem is ConnectionType.NETWORK;

        autoResume.IsCheckedChanged += (_, _) => autoResumeChecked = autoResume.IsChecked == true;

        // the toggle always mirrors the theme actually in use (also when following the OS theme)
        bool syncingTheme = false;
        void SyncToggle()
        {
            syncingTheme = true;
            darkMode.IsChecked = NoexsApplication.IsDark;
            syncingTheme = false;
        }
        SyncToggle();
        if (Avalonia.Application.Current is { } app)
        {
            app.ActualThemeVariantChanged += (_, _) => SyncToggle();
        }
        darkMode.IsCheckedChanged += (_, _) =>
        {
            if (syncingTheme)
            {
                return;
            }
            bool dark = darkMode.IsChecked == true;
            NoexsApplication.ApplyTheme(dark);
            Settings.SetDarkMode(dark);
        };

        Repeating.Start(TimeSpan.FromMilliseconds(100), () =>
        {
            Debugger d = debugger;
            if (d.Connected && autoResumeChecked)
            {
                d.Resume();
            }
        }, timerCts.Token);

        connectionWatchdog.Start();
    }

    /// <summary>Calls <paramref name="c"/> on every controller; one failing controller doesn't stop the rest.</summary>
    private void Fire(Action<IController> c)
    {
        foreach (IController controller in controllers)
        {
            try
            {
                c(controller);
            }
            catch (Exception e)
            {
                Log.Error(e, controller.GetType().Name);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> every <paramref name="period"/> on a background thread until shutdown.</summary>
    internal void Schedule(TimeSpan period, Action action)
    {
        Repeating.Start(period, action, timerCts.Token);
    }

    public ToolsTab Tools()
    {
        return toolsTabPage;
    }

    public MemoryViewerTab Memory()
    {
        return memViewTabPage;
    }

    public DisassemblerTab Disassembly()
    {
        return disassembleTabPage;
    }

    public SearchTab Search()
    {
        return searchTabPage;
    }

    public PointerSearchTab Pointer()
    {
        return pointerTabPage;
    }

    public WatchlistTab Watch()
    {
        return watchlistTabPage;
    }

    public SignatureTab Signatures()
    {
        return signatureTabPage;
    }

    public DumpParserTab DumpParser()
    {
        return dumpParserTabPage;
    }

    public void SetMainController(MainWindow c)
    {
        //ignored
    }

    public void OnConnect()
    {
        SetConnectBtnText("Disconnect");
        connectionService.Cancel();
        SetStatus("Connected");

        DebuggerStatus? status = debugger.GetStatus();
        SetTitle(status?.Status);
        SetStatus("Connected (noexs " + debugger.ServerVersion + ")");
        ApplyTransferOptions();
        if (connectionType.SelectedItem is ConnectionType.NETWORK)
        {
            try
            {
                string host = (ipAddr.Text ?? "").Trim();
                Settings.AddHostHistory(host);
                Settings.SetConnectionHost(host);
            }
            catch (Exception e)
            {
                Log.Error(e, "saving the address history");
            }
        }
    }

    /// <summary>Drop-down of recently used addresses; each entry has a ✕ that removes it.</summary>
    private void ShowHostHistory(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        List<string> hosts = Settings.GetHostHistory();
        if (hosts.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = "No saved addresses yet (added after a successful connection)" },
                IsEnabled = false,
            });
        }
        foreach (string host in hosts)
        {
            var remove = new Button
            {
                Content = "✕",
                Padding = new Thickness(6, 0),
                Margin = new Thickness(12, 0, 0, 0),
                MinHeight = 0,
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            ToolTip.SetTip(remove, "Remove " + host);
            Avalonia.Automation.AutomationProperties.SetName(remove, "Remove " + host);
            var header = new DockPanel { MinWidth = 160 };
            DockPanel.SetDock(remove, Dock.Right);
            header.Children.Add(remove);
            // a TextBlock, so "_" isn't taken as an access key
            header.Children.Add(new TextBlock { Text = host, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var item = new MenuItem { Header = header };
            Avalonia.Automation.AutomationProperties.SetName(item, host);
            item.Click += (_, _) => ipAddr.Text = host;
            remove.Click += (_, args) =>
            {
                args.Handled = true; // don't also pick the entry
                Settings.RemoveHostHistory(host);
                menu.Items.Remove(item);
                if (menu.Items.Count == 0)
                {
                    menu.Close();
                }
                SetStatus("Removed " + host + " from the address history");
            };
            menu.Items.Add(item);
        }
        menu.Open(ipHistoryBtn);
    }

    /// <summary>Sends the dump transfer options (Utils tab) to the sysmodule, if connected.</summary>
    public void ApplyTransferOptions()
    {
        if (!debugger.Connected)
        {
            return;
        }
        DumpCompression compression = Settings.GetDumpCompression();
        bool doubleBuffering = Settings.GetDoubleBuffering();
        Debugger d = debugger;
        Task.Run(() =>
        {
            try
            {
                d.SetTransferOptions(compression, doubleBuffering);
                Log.Info($"transfer options: compression {d.Compression.Label()}, double buffering {d.DoubleBuffering}");
            }
            catch (Exception e)
            {
                Log.Error(e, "setting the transfer options");
            }
        });
    }

    public void OnDisconnect()
    {
        SetTitle(null);
        SetConnectBtnText("Connect");
        SetStatus("Connection Closed");
    }

    public void Stop()
    {
        if (stopped)
        {
            return;
        }
        stopped = true;
        connectionWatchdog.Stop();
        timerCts.Cancel();
        connectionService.Cancel();
        foreach (IController c in controllers)
        {
            if (c == this)
            {
                continue;
            }
            try
            {
                c.Stop();
            }
            catch (Exception e)
            {
                Log.Error(e, "stopping " + c.GetType().Name);
            }
        }
        try
        {
            Settings.SetConnectionHost(ipAddr.Text ?? "");
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
        debugger.Dispose(); // never throws
    }

    /// <summary>
    /// Central handler for exceptions that escaped an event handler. Connection failures reset the
    /// UI to the disconnected state instead of piling up error dialogs.
    /// </summary>
    internal void OnUnhandledException(Exception e)
    {
        if (sessionActive && !debugger.Connected)
        {
            HandleConnectionLost();
            return;
        }
        MessageDialog.ShowMessage(e);
    }

    /// <summary>Runs <paramref name="action"/>, routing any exception through <see cref="OnUnhandledException"/>.</summary>
    internal bool Guard(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e)
        {
            Log.Error(e);
            OnUnhandledException(e);
            return false;
        }
    }

    /// <summary>Shows the Java app's "not connected" warning when there is no live connection.</summary>
    internal bool RequireConnection()
    {
        if (debugger.Connected)
        {
            return true;
        }
        MessageDialog.ShowMessage("You are not currently connected.", AlertType.WARNING);
        return false;
    }

    private void HandleConnectionLost()
    {
        if (!sessionActive)
        {
            return;
        }
        sessionActive = false;
        Log.Info("connection lost");
        debugger.Dispose();
        Fire(c => c.OnDisconnect());
        ipAddr.IsEnabled = connectionType.SelectedItem is ConnectionType.NETWORK;
        connectionType.IsEnabled = true;
        connectBtn.IsEnabled = true;
        SetStatus("Connection lost!");
    }

    public void SetTitle(string? title)
    {
        RunOnUi(() => Title = NoexsApplication.APP_NAME + " v" + NoexsApplication.APP_VERSION +
                              (title != null ? " - " + title : ""));
    }

    public void SetStatus(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }
        RunOnUi(() => statusLbl.Text = message);
    }

    public void SetConnectBtnText(string message)
    {
        RunOnUi(() => connectBtn.Content = message);
    }

    /// <summary>Shows <paramref name="service"/>'s progress in the progress bar (progressProperty().bind()).</summary>
    public void BindProgress(BackgroundService service)
    {
        UnbindProgress();
        boundProgress = service;
        service.ProgressChanged += SetProgress;
        SetProgress(service.Progress);
    }

    public void UnbindProgress()
    {
        if (boundProgress != null)
        {
            boundProgress.ProgressChanged -= SetProgress;
            boundProgress = null;
        }
    }

    public void SetProgress(double value)
    {
        progressBar.IsIndeterminate = value < 0;
        progressBar.Value = Math.Max(0, value);
        int i = (int)(value * 100);
        progressLabel.Text = Math.Clamp(i, 0, 100) + "%";
    }

    private void ContinueProcess(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        Guard(() =>
        {
            Result res = debugger.Resume();
            if (res.Succeeded)
            {
                SetStatus("Resumed");
            }
            else
            {
                MessageDialog.ShowMessage("Resume failed", res, AlertType.WARNING);
            }
        });
    }

    private void PauseGame(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        Guard(() =>
        {
            Result res = debugger.Pause();
            if (res.Succeeded)
            {
                SetStatus("Paused");
            }
            else
            {
                MessageDialog.ShowMessage("Pause failed", res, AlertType.WARNING);
            }
        });
    }

    public void RunAndWait(Action? r)
    {
        if (r == null)
        {
            return;
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            r();
            return;
        }
        Dispatcher.UIThread.Invoke(r);
    }

    private static void RunOnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    }

    private void SetConn(Debugger conn)
    {
        debugger = conn;
        sessionActive = true;
        try
        {
            // the main controller first: it validates the protocol version
            OnConnect();
        }
        catch (Exception e)
        {
            Log.Error(e, "connect handshake");
            sessionActive = false;
            debugger.Dispose();
            Fire(c => c.OnDisconnect());
            ipAddr.IsEnabled = connectionType.SelectedItem is ConnectionType.NETWORK;
            connectionType.IsEnabled = true;
            SetStatus("Unable to Connect!");
            MessageDialog.ShowMessage(e);
            return;
        }
        foreach (IController c in controllers.Where(c => c != this))
        {
            try
            {
                c.OnConnect();
            }
            catch (Exception e)
            {
                Log.Error(e, c.GetType().Name);
            }
        }
    }

    private void Connect(object? sender, RoutedEventArgs e)
    {
        connectionService.OnSucceeded = conn => SetConn(new Debugger(conn));
        connectionService.OnFailed = ex =>
        {
            SetStatus("Unable to Connect!");
            if (ex is Noexs.IO.Usb.UsbException or DllNotFoundException)
            {
                // USB problems usually need user action (driver, cable, sysmodule) - say which
                MessageDialog.ShowMessage("USB connection failed:\n\n" + ex.Message, AlertType.WARNING);
            }
            SetConnectBtnText("Connect");
            connectionType.IsEnabled = true;
            ipAddr.IsEnabled = connectionType.SelectedItem is ConnectionType.NETWORK;
        };

        bool disabled = false;
        if (connectionService.IsRunning)
        {
            SetConnectBtnText("Connect");
            connectionService.Cancel();
            SetStatus("Connect Canceled");
        }
        else if (debugger.Connected) // Disconnect
        {
            sessionActive = false;
            debugger.Dispose();
            Fire(c => c.OnDisconnect());
            connectBtn.IsEnabled = true;
        }
        else
        {
            disabled = true;
            SetConnectBtnText("Cancel");
            connectionService.SetType(connectionType.SelectedItem is ConnectionType t ? t : ConnectionType.NETWORK);
            connectionService.SetHost((ipAddr.Text ?? "").Trim());
            connectionService.SetPort(NetworkConstants.DEFAULT_PORT);
            connectionService.Restart();
        }
        ipAddr.IsEnabled = !disabled && connectionType.SelectedItem is ConnectionType.NETWORK;
        connectionType.IsEnabled = !disabled;
    }

    /// <summary>Shows an open/save dialog and remembers the chosen directory. Returns null on cancel or error.</summary>
    internal async Task<string?> BrowseFile(bool save, string title, string desc, params string[] extensions)
    {
        try
        {
            IStorageProvider sp = StorageProvider;
            IStorageFolder? start = await sp.TryGetFolderFromPathAsync(chooserDir);
            var filter = new FilePickerFileType(desc) { Patterns = extensions };
            string? path;
            if (save)
            {
                IStorageFile? f = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = title,
                    FileTypeChoices = [filter],
                    SuggestedStartLocation = start,
                    DefaultExtension = extensions.Length > 0 ? extensions[0].TrimStart('*', '.') : null,
                });
                path = f?.TryGetLocalPath();
            }
            else
            {
                IReadOnlyList<IStorageFile> files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    FileTypeFilter = [filter],
                    SuggestedStartLocation = start,
                    AllowMultiple = false,
                });
                path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            }

            if (path != null && Path.GetDirectoryName(path) is { } parent)
            {
                chooserDir = parent;
                Settings.SetChooserFile(parent);
            }
            return path;
        }
        catch (Exception e)
        {
            Log.Error(e, "file dialog");
            MessageDialog.ShowMessage(e);
            return null;
        }
    }

    internal Debugger GetDebugger()
    {
        return debugger;
    }

    public void SetTab(Tab tab)
    {
        tabs.SelectedIndex = (int)tab;
    }

    public Tab GetCurrentTab()
    {
        return (Tab)Math.Max(0, tabs.SelectedIndex);
    }
}
