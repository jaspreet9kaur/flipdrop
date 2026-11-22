using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
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
    private string _effect = "Rainbow";

    public SettingsWindow()
    {
        InitializeComponent();

        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseBtn.Click += (_, _) => Close(); // OnClosing hides to tray

        NavPair.Checked += (_, _) => ShowPanel(PanelPair);
        NavEffects.Checked += (_, _) => ShowPanel(PanelEffects);
        NavReceived.Checked += (_, _) => ShowPanel(PanelReceived);
        NavDelivery.Checked += (_, _) => ShowPanel(PanelDelivery);
        NavAppearance.Checked += (_, _) => ShowPanel(PanelAppearance);

        EdgeRight.Checked += (_, _) => SetEdge(EntryEdge.Right);
        EdgeLeft.Checked += (_, _) => SetEdge(EntryEdge.Left);
        EdgeTop.Checked += (_, _) => SetEdge(EntryEdge.Top);
        EdgeBottom.Checked += (_, _) => SetEdge(EntryEdge.Bottom);

        ThemeSystem.Checked += (_, _) => SetTheme("System");
        ThemeLight.Checked += (_, _) => SetTheme("Light");
        ThemeDark.Checked += (_, _) => SetTheme("Dark");

        AnimFull.Checked += (_, _) => SetAnimation("Full");
        AnimSubtle.Checked += (_, _) => SetAnimation("Subtle");
        AnimOff.Checked += (_, _) => SetAnimation("Off");

        LaptopName.Text = Environment.MachineName;
        SecurityCodeText.Text = _app.Cert.SecurityCode;
        IpList.ItemsSource = GetLocalIPv4();

        LoadFromSettings();
        _app.Devices.Changed += OnDevicesChanged;
        _app.Pairing.CodeChanged += OnCodeChanged;
        _app.Pairing.LockedOut += OnLockedOut;
        _app.History.Changed += OnHistoryChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        RefreshDevices();
        RefreshHistory();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => { UpdatePairing(); UpdateLockBanner(); };
        _timer.Start();
        UpdatePairing();
        UpdateLockBanner();

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

        switch (s.Theme)
        {
            case "Light": ThemeLight.IsChecked = true; break;
            case "Dark": ThemeDark.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }
        switch (s.Animation)
        {
            case "Subtle": AnimSubtle.IsChecked = true; break;
            case "Off": AnimOff.IsChecked = true; break;
            default: AnimFull.IsChecked = true; break;
        }
    }

    // ---------- panels ----------

    private void ShowPanel(UIElement panel)
    {
        PanelPair.Visibility = Visibility.Collapsed;
        PanelEffects.Visibility = Visibility.Collapsed;
        PanelReceived.Visibility = Visibility.Collapsed;
        PanelDelivery.Visibility = Visibility.Collapsed;
        PanelAppearance.Visibility = Visibility.Collapsed;
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

    private void OnLockedOut(object? sender, PairingLockedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateLockBanner);

    private void UpdateLockBanner()
    {
        var until = _app.Pairing.LockedUntil;
        if (until is { } u && u > DateTimeOffset.Now)
        {
            int secs = (int)Math.Ceiling((u - DateTimeOffset.Now).TotalSeconds);
            LockBannerText.Text = $"Someone typed wrong pairing codes — pairing paused for {secs} s.";
            LockBanner.Visibility = Visibility.Visible;
        }
        else
        {
            LockBanner.Visibility = Visibility.Collapsed;
        }
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
                Sub = BuildDeviceSub(d)
            })
            .ToList();
        DevicesList.ItemsSource = rows;
        EmptyDevices.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildDeviceSub(PairedDevice d)
    {
        var sub = $"paired {d.PairedAt.LocalDateTime:MMM d, HH:mm}";
        if (d.LastSeenUtc is { } seen)
            sub += $"  ·  last seen {seen.LocalDateTime:MMM d, HH:mm}";
        return sub;
    }

    private void OnRevokeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id)
            _app.Devices.Revoke(id);
    }

    // ---------- received history ----------

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(RefreshHistory);

    private void RefreshHistory()
    {
        var rows = _app.History.Items.Select(h =>
        {
            var accent = h.IsRisky ? FileIcons.RiskyAccent : FileIcons.Accent(h.Kind);
            return new HistoryRow
            {
                Name = h.Name,
                Path = h.Path,
                Sub = $"{FileIcons.Label(h.Kind)}  ·  {FileTypes.FormatSize(h.Size)}  ·  " +
                      $"{h.Time.LocalDateTime:MMM d, HH:mm}  ·  {h.Device}",
                IconGeometry = FileIcons.Geometry(h.Kind, h.IsRisky),
                IconBrush = new SolidColorBrush(accent),
                Tint = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)),
                OpenVisibility = h.IsRisky ? Visibility.Collapsed : Visibility.Visible,
            };
        }).ToList();
        HistoryList.ItemsSource = rows;
        EmptyHistory.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHistoryOpen(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string path) return;
        var ext = System.IO.Path.GetExtension(path).TrimStart('.');
        if (FileTypes.IsRiskyExtension(ext)) return; // never shell-open a risky type
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void OnHistoryFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string path) return;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add("/select,");
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* ignore */ }
    }

    private void OnClearHistory(object sender, RoutedEventArgs e) => _app.History.Clear();

    // ---------- effects ----------

    private void OnPickRainbow(object s, RoutedEventArgs e) => SelectEffect("Rainbow");
    private void OnPickPortal(object s, RoutedEventArgs e) => SelectEffect("Portal");
    private void OnPickComet(object s, RoutedEventArgs e) => SelectEffect("Comet");
    private void OnPickRipple(object s, RoutedEventArgs e) => SelectEffect("Ripple");

    private void SelectEffect(string name, bool save = true)
    {
        _effect = name;
        var accent = (Brush)FindResource("AccentBrush");
        var normal = (Brush)FindResource("CardBorder");

        CardRainbow.BorderBrush = name == "Rainbow" ? accent : normal;
        CardPortal.BorderBrush = name == "Portal" ? accent : normal;
        CardComet.BorderBrush = name == "Comet" ? accent : normal;
        CardRipple.BorderBrush = name == "Ripple" ? accent : normal;
        CheckRainbow.Visibility = name == "Rainbow" ? Visibility.Visible : Visibility.Collapsed;
        CheckPortal.Visibility = name == "Portal" ? Visibility.Visible : Visibility.Collapsed;
        CheckComet.Visibility = name == "Comet" ? Visibility.Visible : Visibility.Collapsed;
        CheckRipple.Visibility = name == "Ripple" ? Visibility.Visible : Visibility.Collapsed;

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

    // ---------- appearance ----------

    private void SetTheme(string theme)
    {
        if (_loading) return;
        _app.Settings.Theme = theme;
        _app.Settings.Save();
        ThemeManager.Apply(theme);
    }

    private void SetAnimation(string animation)
    {
        if (_loading) return;
        _app.Settings.Animation = animation;
        _app.Settings.Save();
    }

    private void OnThemeChanged()
    {
        // Resource brushes swapped — refresh the code-set effect-card borders.
        SelectEffect(_effect, save: false);
    }

    // ---------- delivery ----------

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "Choose where flung files are saved",
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

    private sealed class HistoryRow
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public string Sub { get; init; } = "";
        public Geometry? IconGeometry { get; init; }
        public Brush? IconBrush { get; init; }
        public Brush? Tint { get; init; }
        public Visibility OpenVisibility { get; init; }
    }
}
