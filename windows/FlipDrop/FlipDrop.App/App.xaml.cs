using System.Windows;
using FlipDrop.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FlipDrop.App;

public partial class App : System.Windows.Application
{
    public static new App Current => (App)System.Windows.Application.Current;

    public AppSettings Settings { get; private set; } = null!;
    public CertificateManager Cert { get; private set; } = null!;
    public DeviceStore Devices { get; private set; } = null!;
    public PairingService Pairing { get; private set; } = null!;
    public FlipDropServer Server { get; private set; } = null!;
    public HistoryStore History { get; private set; } = null!;

    private DiscoveryService? _discovery;
    private Forms.NotifyIcon? _tray;
    private SettingsWindow? _settings;
    private OverlayWindow? _overlay;

    private OverlayWindow Overlay => _overlay ??= new OverlayWindow();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, ex) =>
        {
            // Keep the receiver alive if a single animation misbehaves.
            ex.Handled = true;
        };

        AppPaths.EnsureDataDir();
        Settings = AppSettings.Load();
        History = new HistoryStore(System.IO.Path.Combine(AppPaths.DataDir, "history.json"), () => Settings.SaveFolder);

        ThemeManager.Apply(Settings.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        try
        {
            Cert = new CertificateManager(AppPaths.DataDir);
            Devices = new DeviceStore(AppPaths.DevicesFile);
            Pairing = new PairingService(Devices);
            Server = new FlipDropServer(Cert, Pairing, Devices, () => Settings.SaveFolder);
            Server.TransferStarted += OnTransferStarted;
            Server.TransferProgress += OnTransferProgress;
            Server.FileReceived += OnFileReceived;
            Server.TransferFailed += OnTransferFailed;
            await Server.StartAsync();

            _discovery = new DiscoveryService(Cert);
            _discovery.Start();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "FlipDrop could not start its network services.\n\n" + ex.Message,
                "FlipDrop", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        CreateTray();

        if (Environment.GetEnvironmentVariable("FLIPDROP_START_HIDDEN") != "1")
            ShowSettings();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Follow the Windows light/dark theme when the user chose "System".
        if (e.Category == UserPreferenceCategory.General &&
            string.Equals(Settings.Theme, "System", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(Settings.Theme));
        }
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "FlipDrop",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open FlipDrop", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    public void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }
        if (!_settings.IsVisible) _settings.Show();
        _settings.Activate();
        if (_settings.WindowState == WindowState.Minimized)
            _settings.WindowState = WindowState.Normal;
    }

    private void QuitApp()
    {
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _tray?.Dispose();
            _discovery?.Dispose();
            _ = Server?.DisposeAsync();
        }
        catch { /* best effort */ }
        Shutdown();
    }

    // ---------- transfer events (raised on thread-pool threads → marshal to the dispatcher) ----------

    private void OnTransferStarted(object? sender, TransferStartedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ToastWindow.ShowStarted(e));

    private void OnTransferProgress(object? sender, TransferProgressEventArgs e) =>
        Dispatcher.BeginInvoke(() => ToastWindow.UpdateProgress(e));

    private void OnTransferFailed(object? sender, TransferFailedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ToastWindow.ShowFailed(e));

    private void OnFileReceived(object? sender, FileReceivedEventArgs e) =>
        Dispatcher.BeginInvoke(() => HandleFileReceived(e));

    private void HandleFileReceived(FileReceivedEventArgs e)
    {
        // Toast immediately, independent of the landing animation.
        ToastWindow.ShowLanded(e);

        History.Add(new HistoryEntry
        {
            Name = e.FileName,
            Path = e.FilePath,
            Kind = e.Kind,
            Size = e.SizeBytes,
            Time = DateTimeOffset.Now,
            Device = e.DeviceName,
            IsRisky = e.IsRisky
        });

        if (Settings.PlaySound)
            Whoosh.Play();

        // Fire-and-forget overlay; it never blocks the next file.
        Overlay.Play(e, Settings.Effect, Settings.Edge, Settings.Animation);
    }

    public void PlayTest()
    {
        var sample = SampleImage.CreateSampleFile();
        var id = Guid.NewGuid();
        long size = new System.IO.FileInfo(sample).Length;

        var started = new TransferStartedEventArgs
        {
            TransferId = id,
            DisplayName = System.IO.Path.GetFileName(sample),
            Kind = FileKind.Image,
            TotalBytes = size,
            DeviceName = "This laptop",
            FlingX = 0.5,
            FlingVelocity = 2600
        };
        var received = new FileReceivedEventArgs
        {
            TransferId = id,
            FilePath = sample,
            FileName = System.IO.Path.GetFileName(sample),
            Kind = FileKind.Image,
            SizeBytes = size,
            IsRisky = false,
            DeviceName = "This laptop",
            FlingX = 0.5,
            FlingVelocity = 2600
        };

        Dispatcher.BeginInvoke(() =>
        {
            ToastWindow.ShowStarted(started);
            HandleFileReceived(received);
        });
    }
}
