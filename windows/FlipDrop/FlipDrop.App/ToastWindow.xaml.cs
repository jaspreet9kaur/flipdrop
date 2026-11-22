using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlipDrop.Core;

namespace FlipDrop.App;

/// <summary>
/// A corner toast for one transfer. Appears the instant a transfer starts (independent of the
/// landing overlay), shows a live progress bar for larger incoming files, then morphs into a
/// "Landed" card with Open / Show in folder. Up to three stack in the corner; each auto-dismisses
/// after ~6 s, and hovering pauses that countdown.
/// </summary>
public partial class ToastWindow : Window
{
    private const double EdgeMargin = 14;
    private const double Gap = 4;
    private const int MaxVisible = 3;
    private const long ProgressThresholdBytes = 2L * 1024 * 1024;
    private static readonly TimeSpan DismissAfter = TimeSpan.FromSeconds(6);

    private static readonly List<ToastWindow> Active = new();
    private static readonly Dictionary<Guid, ToastWindow> ById = new();

    private readonly Guid _id;
    private readonly DispatcherTimer _life;
    private string? _filePath;
    private FileKind _kind;
    private bool _risky;
    private bool _landed;
    private bool _closing;

    private ToastWindow(Guid id)
    {
        _id = id;
        InitializeComponent();

        _life = new DispatcherTimer { Interval = DismissAfter };
        _life.Tick += (_, _) => BeginClose();

        Card.MouseEnter += (_, _) => { if (_landed) _life.Stop(); };
        Card.MouseLeave += (_, _) => { if (_landed && !_closing) RestartLife(); };
        CloseBtn.Click += (_, _) => BeginClose();
        OpenBtn.Click += (_, _) => OpenFile();
        FolderBtn.Click += (_, _) => ShowInFolder();

        Loaded += OnLoaded;
        SizeChanged += (_, _) => Reflow();
    }

    // ---------- static entry points (call on the UI thread) ----------

    public static void ShowStarted(TransferStartedEventArgs e)
    {
        var t = GetOrCreate(e.TransferId);
        t.ApplyReceiving(e.DisplayName, e.Kind, e.TotalBytes, e.DeviceName);
    }

    public static void UpdateProgress(TransferProgressEventArgs e)
    {
        if (ById.TryGetValue(e.TransferId, out var t))
            t.ApplyProgress(e.BytesReceived, e.TotalBytes);
    }

    public static void ShowLanded(FileReceivedEventArgs e)
    {
        var t = GetOrCreate(e.TransferId);
        t.ApplyLanded(e.FilePath, e.FileName, e.Kind, e.SizeBytes, e.IsRisky, e.DeviceName);
    }

    public static void ShowFailed(TransferFailedEventArgs e)
    {
        if (ById.TryGetValue(e.TransferId, out var t))
            t.ApplyFailed(e.Reason);
    }

    private static ToastWindow GetOrCreate(Guid id)
    {
        if (ById.TryGetValue(id, out var existing)) return existing;

        var t = new ToastWindow(id);
        ById[id] = t;
        t.Show();
        return t;
    }

    // ---------- states ----------

    private void ApplyReceiving(string name, FileKind kind, long totalBytes, string device)
    {
        if (_landed) return;
        _kind = kind;
        NameText.Text = name;
        StatusLabel.Text = string.IsNullOrWhiteSpace(device) ? "Receiving…" : $"Receiving from {device}";
        SetKindChip(kind, risky: false);
        ShowIcon(kind, risky: false);

        if (totalBytes > ProgressThresholdBytes)
        {
            ProgressHost.Visibility = Visibility.Visible;
            MetaText.Text = $"0 / {FileTypes.FormatSize(totalBytes)}";
        }
        else
        {
            ProgressHost.Visibility = Visibility.Collapsed;
            MetaText.Text = totalBytes > 0 ? FileTypes.FormatSize(totalBytes) : "";
        }
    }

    private void ApplyProgress(long received, long total)
    {
        if (_landed || total <= 0) return;
        double pct = Math.Clamp((double)received / total, 0, 1);
        var anim = new DoubleAnimation(pct, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        if (ProgressHost.Visibility == Visibility.Visible)
            MetaText.Text = $"{FileTypes.FormatSize(received)} / {FileTypes.FormatSize(total)}";
    }

    private void ApplyLanded(string path, string fileName, FileKind kind, long size, bool risky, string device)
    {
        _landed = true;
        _filePath = path;
        _kind = kind;
        _risky = risky;

        StatusLabel.Text = "Landed in FlipDrop";
        NameText.Text = fileName;
        ProgressHost.Visibility = Visibility.Collapsed;
        ActionHost.Visibility = Visibility.Visible;

        SetKindChip(kind, risky);
        MetaText.Text = risky ? FileTypes.FormatSize(size)
                              : $"{FileTypes.FormatSize(size)}  ·  {device}";

        OpenBtn.Visibility = OpenPolicy.CanOpen(path, kind) ? Visibility.Visible : Visibility.Collapsed;

        ShowIcon(kind, risky);
        _ = LoadThumbnailAsync(path, kind, risky);

        RestartLife();
    }

    private void ApplyFailed(string reason)
    {
        _landed = true; // stop progress/thumbnail work
        StatusLabel.Text = "Transfer failed";
        MetaText.Text = reason;
        ProgressHost.Visibility = Visibility.Collapsed;
        ActionHost.Visibility = Visibility.Collapsed;
        SetKindChip(_kind, risky: false);
        RestartLife();
    }

    // ---------- visuals ----------

    private void SetKindChip(FileKind kind, bool risky)
    {
        var accent = risky ? FileIcons.RiskyAccent : FileIcons.Accent(kind);
        KindDot.Fill = new SolidColorBrush(accent);
        KindChip.Background = new SolidColorBrush(Color.FromArgb(0x24, accent.R, accent.G, accent.B));
        KindText.Foreground = new SolidColorBrush(accent);
        KindText.Text = risky ? "Program/script — not opened" : FileIcons.Label(kind);
    }

    private void ShowIcon(FileKind kind, bool risky)
    {
        var accent = risky ? FileIcons.RiskyAccent : FileIcons.Accent(kind);
        ThumbImage.Visibility = Visibility.Collapsed;
        ThumbImage.Source = null;
        ThumbHost.Background = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B));
        IconHost.Visibility = Visibility.Visible;
        IconHost.Content = FileIcons.Build(kind, risky, 30, new SolidColorBrush(accent));
    }

    private async Task LoadThumbnailAsync(string path, FileKind kind, bool risky)
    {
        if (risky) return; // never generate shell thumbnails for programs/scripts

        ImageSource? img = null;
        if (kind == FileKind.Image)
            img = await ImageDecoder.DecodeAsync(path, 160);
        else if (kind == FileKind.Video)
            // Only videos get a shell thumbnail. PDFs/documents/others keep their vector type icon so
            // attacker-controlled document/PDF content is never parsed in-process to make a preview.
            img = await ShellThumbnails.TryGetAsync(path, 108);

        if (img is null || _closing) return;
        ThumbImage.Source = img;
        ThumbImage.Visibility = Visibility.Visible;
        ThumbHost.Background = Brushes.Transparent;
        IconHost.Visibility = Visibility.Collapsed;
    }

    // ---------- lifecycle ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Native.MakeToolWindow(this);
        Active.Add(this);
        // Retire the oldest live toasts until at most MaxVisible are on screen. A burst can add several
        // before earlier ones finish their close animation and leave Active, so closing just Active[0]
        // (which may already be closing) can leave more than MaxVisible stacked. Count live (non-closing)
        // toasts and close oldest-first until within the cap.
        int live = 0;
        foreach (var t in Active)
            if (!t._closing) live++;
        for (int i = 0; i < Active.Count && live > MaxVisible; i++)
        {
            if (!Active[i]._closing) { Active[i].BeginClose(); live--; }
        }
        Reflow();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase() };
        var slide = new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(420))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var pop = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(420))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
        BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.XProperty, slide);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void RestartLife()
    {
        _life.Stop();
        _life.Start();
    }

    private void Reflow()
    {
        var wa = SystemParameters.WorkArea;
        double y = wa.Bottom - EdgeMargin;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var t = Active[i];
            double h = t.ActualHeight > 0 ? t.ActualHeight : t.Height;
            t.Left = wa.Right - EdgeMargin - t.Width;
            t.Top = y - h;
            y -= (h + Gap);
        }
    }

    private void OpenFile()
    {
        if (_risky || _filePath is null) return;
        if (!OpenPolicy.CanOpen(_filePath, _kind)) return; // allowlist gate: only safe, viewer-friendly types
        try
        {
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch { /* ignore */ }
        BeginClose();
    }

    private void ShowInFolder()
    {
        if (_filePath is null) return;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add("/select,");
            psi.ArgumentList.Add(_filePath);
            Process.Start(psi);
        }
        catch { /* ignore */ }
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();
        ById.Remove(_id);

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        var slide = new DoubleAnimation(Slide.X, 28, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            Active.Remove(this);
            Reflow();
            Close();
        };
        BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.XProperty, slide);
    }
}
