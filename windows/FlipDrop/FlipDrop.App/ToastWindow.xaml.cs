using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FlipDrop.App;

public partial class ToastWindow : Window
{
    private const double EdgeMargin = 22;
    private const double Gap = 12;
    private static readonly List<ToastWindow> Active = new();

    private readonly string _filePath;
    private readonly DispatcherTimer _life;
    private bool _closing;

    private ToastWindow(string filePath)
    {
        _filePath = filePath;
        InitializeComponent();

        Thumb.ImageSource = SafeLoad(filePath);
        Card.MouseLeftButtonUp += (_, _) => OpenFile();
        CloseBtn.Click += (_, _) => BeginClose();

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _life.Tick += (_, _) => BeginClose();

        Loaded += OnLoaded;
    }

    public static void ShowFor(string filePath)
    {
        var w = new ToastWindow(filePath);
        w.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Native.MakeToolWindow(this);
        Active.Add(this);
        Reflow();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase() };
        var rise = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.YProperty, rise);

        _life.Start();
    }

    private void Reflow()
    {
        var wa = SystemParameters.WorkArea;
        double y = wa.Bottom - EdgeMargin - Height;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var t = Active[i];
            t.Left = wa.Right - EdgeMargin - t.Width;
            t.Top = y;
            y -= (t.Height + Gap);
        }
    }

    private void OpenFile()
    {
        try
        {
            // Open the saved file only, with the shell default viewer. Nothing is executed automatically.
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch { /* ignore */ }
        BeginClose();
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            Active.Remove(this);
            Reflow();
            Close();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private static ImageSource? SafeLoad(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelHeight = 320;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }
}
