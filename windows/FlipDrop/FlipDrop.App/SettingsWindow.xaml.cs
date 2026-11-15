using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FlipDrop.Core;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace FlipDrop.App;

public partial class SettingsWindow : Window
{
    private readonly App _app = App.Current;
    private readonly DispatcherTimer _timer;
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseBtn.Click += (_, _) => Close(); // OnClosing hides to tray

        NavPair.Checked += (_, _) => ShowPanel(PanelPair);
        NavEffects.Checked += (_, _) => ShowPanel(PanelEffects);
        NavDelivery.Checked += (_, _) => ShowPanel(PanelDelivery);

        EdgeRight.Checked += (_, _) => SetEdge(EntryEdge.Right);
        EdgeLeft.Checked += (_, _) => SetEdge(EntryEdge.Left);
        EdgeTop.Checked += (_, _) => SetEdge(EntryEdge.Top);
        EdgeBottom.Checked += (_, _) => SetEdge(EntryEdge.Bottom);

        LaptopName.Text = Environment.MachineName;
        IpList.ItemsSource = GetLocalIPv4();

        LoadFromSettings();
        _app.Devices.Changed += OnDevicesChanged;
        _app.Pairing.CodeChanged += OnCodeChanged;
        RefreshDevices();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdatePairing();
        _timer.Start();
        UpdatePairing();

        _loading = false;
    }

    private void LoadFromSettings()
    {
        var s = _app.Settings;
        SaveFolderText.Text = s.SaveFolder;
        SoundToggle.IsChecked = s.PlaySound;
        switch (s.Edge)
        {
            case EntryEdge.Left: EdgeLeft.IsChecked = true; break;
            case EntryEdge.Top: EdgeTop.IsChecked = true; break;
            case EntryEdge.Bottom: EdgeBottom.IsChecked = true; break;
            default: EdgeRight.IsChecked = true; break;
        }
        SelectEffect(s.Effect, save: false);
    }

    // ---------- panels ----------

    private void ShowPanel(UIElement panel)
    {
        PanelPair.Visibility = Visibility.Collapsed;
        PanelEffects.Visibility = Visibility.Collapsed;
        PanelDelivery.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
    }

    // ---------- pairing ----------

    private void OnCodeChanged() => Dispatcher.BeginInvoke(UpdatePairing);

    private void UpdatePairing()
    {
        CodeText.Text = _app.Pairing.CurrentCode;
        var remaining = _app.Pairing.TimeRemaining;
        CountdownText.Text = $"resets in {Math.Ceiling(remaining.TotalSeconds):0}s";
        double frac = Math.Clamp(remaining.TotalSeconds / PairingService.RotationPeriod.TotalSeconds, 0, 1);
        UpdateRing(frac);
    }

    private void UpdateRing(double fraction)
    {
        const double cx = 84, cy = 84, r = 79;
        if (fraction <= 0.001) { RingPath.Data = null; return; }
        fraction = Math.Min(fraction, 0.9999);
        double sweep = 360 * fraction;
        double a0 = -90 * Math.PI / 180.0;
        double a1 = (-90 + sweep) * Math.PI / 180.0;
        var start = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
        var end = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 180,
            SweepDirection.Clockwise, true));
        RingPath.Data = new PathGeometry(new[] { fig });
    }

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(RefreshDevices);

    private void RefreshDevices()
    {
        var rows = _app.Devices.Devices
            .OrderByDescending(d => d.PairedAt)
            .Select(d => new DeviceRow
            {
                Id = d.DeviceId,
                Name = string.IsNullOrWhiteSpace(d.DeviceName) ? "Unknown device" : d.DeviceName,
                Sub = $"paired {d.PairedAt.LocalDateTime:MMM d, HH:mm}"
            })
            .ToList();
        DevicesList.ItemsSource = rows;
        EmptyDevices.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRevokeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id)
            _app.Devices.Revoke(id);
    }

    // ---------- effects ----------

    private void OnPickRainbow(object s, RoutedEventArgs e) => SelectEffect("Rainbow");
    private void OnPickPortal(object s, RoutedEventArgs e) => SelectEffect("Portal");
    private void OnPickComet(object s, RoutedEventArgs e) => SelectEffect("Comet");

    private void SelectEffect(string name, bool save = true)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var normal = (Brush)FindResource("CardBorder");

        CardRainbow.BorderBrush = name == "Rainbow" ? accent : normal;
        CardPortal.BorderBrush = name == "Portal" ? accent : normal;
        CardComet.BorderBrush = name == "Comet" ? accent : normal;
        CheckRainbow.Visibility = name == "Rainbow" ? Visibility.Visible : Visibility.Collapsed;
        CheckPortal.Visibility = name == "Portal" ? Visibility.Visible : Visibility.Collapsed;
        CheckComet.Visibility = name == "Comet" ? Visibility.Visible : Visibility.Collapsed;

        if (save && !_loading)
        {
            _app.Settings.Effect = name;
            _app.Settings.Save();
        }
    }

    private void SetEdge(EntryEdge edge)
    {
        if (_loading) return;
        _app.Settings.Edge = edge;
        _app.Settings.Save();
    }

    private void OnTestClick(object sender, RoutedEventArgs e) => _app.PlayTest();

    // ---------- delivery ----------

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "Choose where flung photos are saved",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_app.Settings.SaveFolder) ? _app.Settings.SaveFolder : AppPaths.DefaultReceiveDir
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            _app.Settings.SaveFolder = dlg.SelectedPath;
            _app.Settings.Save();
            SaveFolderText.Text = dlg.SelectedPath;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_app.Settings.SaveFolder);
            Process.Start(new ProcessStartInfo(_app.Settings.SaveFolder) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void OnSoundChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.PlaySound = SoundToggle.IsChecked == true;
        _app.Settings.Save();
    }

    // ---------- helpers ----------

    private static List<string> GetLocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address;
                    if (PrivateIp.IsAllowed(ip) && !IPAddress.IsLoopback(ip))
                        list.Add(ip.ToString());
                }
            }
        }
        catch { /* ignore */ }
        if (list.Count == 0) list.Add("127.0.0.1");
        return list.Distinct().ToList();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window hides to the tray rather than quitting. The instance
        // is reused by App.ShowSettings, so timers and subscriptions stay alive.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private sealed class DeviceRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Sub { get; init; } = "";
    }
}
