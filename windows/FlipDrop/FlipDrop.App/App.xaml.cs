using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using FlipDrop.Core;
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

    private DiscoveryService? _discovery;
    private Forms.NotifyIcon? _tray;
    private SettingsWindow? _settings;

    private readonly ConcurrentQueue<PhotoReceivedEventArgs> _queue = new();
    private bool _playing;

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

        try
        {
            Cert = new CertificateManager(AppPaths.DataDir);
            Devices = new DeviceStore(AppPaths.DevicesFile);
            Pairing = new PairingService(Devices);
            Server = new FlipDropServer(Cert, Pairing, Devices, () => Settings.SaveFolder);
            Server.PhotoReceived += OnPhotoReceived;
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
            _tray?.Dispose();
            _discovery?.Dispose();
            _ = Server?.DisposeAsync();
        }
        catch { /* best effort */ }
        Shutdown();
    }

    private void OnPhotoReceived(object? sender, PhotoReceivedEventArgs e)
    {
        // Raised on a Kestrel thread — hop to the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            _queue.Enqueue(e);
            PumpQueue();
        });
    }

    private async void PumpQueue()
    {
        if (_playing) return;
        _playing = true;
        try
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    await PlayOneAsync(item);
                }
                catch { /* never let one photo kill the pump */ }
            }
        }
        finally
        {
            _playing = false;
        }
    }

    private async Task PlayOneAsync(PhotoReceivedEventArgs item)
    {
        if (Settings.PlaySound)
            Whoosh.Play();

        var overlay = new OverlayWindow();
        await overlay.PlayAsync(item, Settings.Effect, Settings.Edge);
        // Hand off to a corner toast that lives on after the overlay closes.
        ToastWindow.ShowFor(item.FilePath);
    }

    public void PlayTest()
    {
        var sample = SampleImage.CreateSampleFile();
        var args = new PhotoReceivedEventArgs
        {
            FilePath = sample,
            FileName = System.IO.Path.GetFileName(sample),
            FlingX = 0.5,
            FlingVelocity = 2600
        };
        Dispatcher.BeginInvoke(() => { _queue.Enqueue(args); PumpQueue(); });
    }
}
