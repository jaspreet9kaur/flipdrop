using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FlipDrop.Core;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace FlipDrop.App;

/// <summary>
/// Full-screen, transparent, click-through, topmost overlay that plays one fling
/// animation (entry flourish -> land at centre -> hold -> glide to the corner) and
/// then closes. The matching corner toast is shown by the caller afterwards.
/// </summary>
public partial class OverlayWindow : Window
{
    private double W, H;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.MakeClickThrough(this);
    }

    public async Task PlayAsync(PhotoReceivedEventArgs item, string effect, EntryEdge edge)
    {
        W = SystemParameters.PrimaryScreenWidth;
        H = SystemParameters.PrimaryScreenHeight;
        Left = 0; Top = 0; Width = W; Height = H;
        Stage.Width = W; Stage.Height = H;

        var bmp = LoadBitmap(item.FilePath, (int)Math.Ceiling(H * 0.6));
        if (bmp is null) { return; }

        Show();

        // --- Landed size (<= 55% screen height, <= 82% width) ---
        double aspect = (double)bmp.PixelWidth / bmp.PixelHeight;
        double landedH = Math.Min(H * 0.55, bmp.PixelHeight);
        double landedW = landedH * aspect;
        if (landedW > W * 0.82) { landedW = W * 0.82; landedH = landedW / aspect; }

        // --- The photo card ---
        var photo = new Border
        {
            Width = landedW,
            Height = landedH,
            CornerRadius = new CornerRadius(18),
            Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
            ClipToBounds = true,
            Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 46, ShadowDepth = 14, Opacity = 0.5, Color = Colors.Black, Direction = 270
            }
        };
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(translate);
        photo.RenderTransform = group;
        photo.RenderTransformOrigin = new Point(0.5, 0.5);
        Canvas.SetLeft(photo, (W - landedW) / 2);
        Canvas.SetTop(photo, (H - landedH) / 2);

        // --- Entry geometry ---
        double frac = Math.Clamp(item.FlingX ?? 0.5, 0, 1);
        Point anchor = EdgeAnchor(edge, frac);                 // where it crosses the edge
        (double sx, double sy) = StartOffset(edge, frac, landedW, landedH);

        double vel = item.FlingVelocity ?? 2000;
        double entryMs = Math.Clamp(950 - vel / 9.0, 380, 900); // faster fling => quicker entry

        // Decorations are drawn behind the photo.
        switch (effect)
        {
            case "Portal": BuildPortal(anchor, entryMs); break;
            case "Comet": BuildComet(anchor, new Point(W / 2, H / 2), entryMs); break;
            default: BuildRainbow(anchor, edge, entryMs); break;
        }
        Stage.Children.Add(photo);

        // --- Entry: fly from off-edge to centre ---
        double startScale = effect == "Portal" ? 0.18 : effect == "Comet" ? 0.34 : 0.55;
        double startAngle = effect == "Comet" ? -14 : effect == "Portal" ? 22 : -6;
        scale.ScaleX = scale.ScaleY = startScale;
        rotate.Angle = startAngle;
        translate.X = sx; translate.Y = sy;

        IEasingFunction entryEase = effect == "Comet"
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } // soft landing bounce
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        Animate(photo, OpacityProperty, 0, 1, Math.Min(entryMs * 0.6, 420), new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(translate, TranslateTransform.XProperty, sx, 0, entryMs, entryEase);
        Animate(translate, TranslateTransform.YProperty, sy, 0, entryMs, entryEase);
        Animate(scale, ScaleTransform.ScaleXProperty, startScale, 1, entryMs, entryEase);
        Animate(scale, ScaleTransform.ScaleYProperty, startScale, 1, entryMs, entryEase);
        Animate(rotate, RotateTransform.AngleProperty, startAngle, 0, entryMs, new CubicEase { EasingMode = EasingMode.EaseOut });

        await Task.Delay((int)entryMs + 40);

        // --- Hold ~1.2 s ---
        await Task.Delay(1200);

        // --- Glide to the bottom-right corner as a shrinking thumbnail ---
        var wa = SystemParameters.WorkArea;
        const double toastW = 236, toastH = 176, margin = 22;
        double cornerCx = wa.Right - margin - toastW / 2;
        double cornerCy = wa.Bottom - margin - toastH / 2;
        double exitScale = Math.Min((toastW - 16) / landedW, (toastH - 44) / landedH);
        double exitMs = 480;
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };

        Animate(translate, TranslateTransform.XProperty, 0, cornerCx - W / 2, exitMs, exitEase);
        Animate(translate, TranslateTransform.YProperty, 0, cornerCy - H / 2, exitMs, exitEase);
        Animate(scale, ScaleTransform.ScaleXProperty, 1, exitScale, exitMs, exitEase);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, exitScale, exitMs, exitEase);
        Animate(photo, OpacityProperty, 1, 0, 180, null, beginMs: exitMs - 140);

        await Task.Delay((int)exitMs + 60);
        Close();
    }

    // ---------- Effect decorations ----------

    private void BuildRainbow(Point anchor, EntryEdge edge, double entryMs)
    {
        // A soft, heavily blurred rainbow streak that sweeps in from the edge.
        double size = Math.Max(W, H) * 0.5;
        var blob = new Ellipse
        {
            Width = size,
            Height = size,
            Opacity = 0,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 110 },
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(230, 255, 170, 120), 0.0),
                    new GradientStop(Color.FromArgb(210, 255, 120, 180), 0.35),
                    new GradientStop(Color.FromArgb(190, 150, 140, 240), 0.65),
                    new GradientStop(Color.FromArgb(0, 150, 140, 240), 1.0),
                }
            }
        };
        var t = new TranslateTransform();
        blob.RenderTransform = t;
        Canvas.SetLeft(blob, anchor.X - size / 2);
        Canvas.SetTop(blob, anchor.Y - size / 2);
        Stage.Children.Add(blob);

        double towardX = (W / 2 - anchor.X) * 0.55;
        double towardY = (H / 2 - anchor.Y) * 0.55;
        Animate(blob, OpacityProperty, 0, 0.95, entryMs * 0.45, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(blob, OpacityProperty, 0.95, 0, entryMs * 0.9, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.5);
        Animate(t, TranslateTransform.XProperty, 0, towardX, entryMs * 1.3, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(t, TranslateTransform.YProperty, 0, towardY, entryMs * 1.3, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    private void BuildPortal(Point anchor, double entryMs)
    {
        // Concentric glowing rings (orange -> pink -> purple), a glow core, twinkling stars.
        var colors = new[]
        {
            Color.FromRgb(255, 150, 70),
            Color.FromRgb(255, 90, 160),
            Color.FromRgb(170, 90, 230),
        };

        var glow = new Ellipse
        {
            Width = 200, Height = 200, Opacity = 0,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 60 },
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(230, 255, 180, 120), 0),
                    new GradientStop(Color.FromArgb(0, 255, 120, 200), 1),
                }
            }
        };
        AddCentered(glow, anchor);
        Animate(glow, OpacityProperty, 0, 0.9, entryMs * 0.5, new CubicEase());
        Animate(glow, OpacityProperty, 0.9, 0, entryMs, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.6);

        for (int i = 0; i < 4; i++)
        {
            double baseD = 120 + i * 70;
            var ring = new Ellipse
            {
                Width = baseD, Height = baseD, Opacity = 0,
                Stroke = new SolidColorBrush(colors[i % colors.Length]),
                StrokeThickness = 10 - i * 1.6,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 8 }
            };
            var rs = new ScaleTransform(0.2, 0.2);
            ring.RenderTransform = rs;
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            AddCentered(ring, anchor);

            double begin = i * 55;
            Animate(ring, OpacityProperty, 0, 0.9, 220, new CubicEase(), beginMs: begin);
            Animate(ring, OpacityProperty, 0.9, 0.0, entryMs, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.65 + begin);
            Animate(rs, ScaleTransform.ScaleXProperty, 0.2, 1.25, entryMs + begin, new CubicEase { EasingMode = EasingMode.EaseOut }, beginMs: begin);
            Animate(rs, ScaleTransform.ScaleYProperty, 0.2, 1.25, entryMs + begin, new CubicEase { EasingMode = EasingMode.EaseOut }, beginMs: begin);
        }

        // Twinkling stars scattered around the portal.
        var rng = new Random();
        for (int i = 0; i < 16; i++)
        {
            double r = 80 + rng.NextDouble() * 220;
            double a = rng.NextDouble() * Math.PI * 2;
            var star = new Ellipse
            {
                Width = 3 + rng.NextDouble() * 3,
                Height = 3 + rng.NextDouble() * 3,
                Fill = Brushes.White,
                Opacity = 0
            };
            Canvas.SetLeft(star, anchor.X + Math.Cos(a) * r);
            Canvas.SetTop(star, anchor.Y + Math.Sin(a) * r * 0.7);
            Stage.Children.Add(star);
            var tw = new DoubleAnimation(0.1, 0.9, TimeSpan.FromMilliseconds(500 + rng.Next(600)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(rng.Next(400))
            };
            star.BeginAnimation(OpacityProperty, tw);
        }

        // A faint comet trail across to the centre.
        BuildComet(anchor, new Point(W / 2, H / 2), entryMs, trailOnly: true);
    }

    private void BuildComet(Point anchor, Point target, double entryMs, bool trailOnly = false)
    {
        double dx = target.X - anchor.X, dy = target.Y - anchor.Y;
        double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;

        var trail = new Rectangle
        {
            Width = len * 1.1,
            Height = 46,
            Opacity = 0,
            RadiusX = 23, RadiusY = 23,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 26 },
            Fill = new LinearGradientBrush(
                Color.FromArgb(0, 255, 160, 90),
                Color.FromArgb(220, 255, 120, 180), new Point(0, 0.5), new Point(1, 0.5))
        };
        trail.RenderTransform = new RotateTransform(angle, 0, 23);
        Canvas.SetLeft(trail, anchor.X);
        Canvas.SetTop(trail, anchor.Y - 23);
        Stage.Children.Add(trail);

        double peak = trailOnly ? 0.5 : 0.85;
        Animate(trail, OpacityProperty, 0, peak, entryMs * 0.35, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(trail, OpacityProperty, peak, 0, entryMs * 0.9, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.4);

        if (trailOnly) return;

        // A bright comet head that races ahead of the photo to the centre.
        var head = new Ellipse
        {
            Width = 40, Height = 40, Opacity = 0,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14 },
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(Color.FromArgb(255, 255, 180, 120), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 120, 180), 1),
                }
            }
        };
        var ht = new TranslateTransform();
        head.RenderTransform = ht;
        Canvas.SetLeft(head, anchor.X - 20);
        Canvas.SetTop(head, anchor.Y - 20);
        Stage.Children.Add(head);
        Animate(head, OpacityProperty, 0, 0.95, 160, new CubicEase());
        Animate(head, OpacityProperty, 0.95, 0, 260, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.55);
        Animate(ht, TranslateTransform.XProperty, 0, dx, entryMs * 0.7, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(ht, TranslateTransform.YProperty, 0, dy, entryMs * 0.7, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    // ---------- helpers ----------

    private void AddCentered(FrameworkElement el, Point p)
    {
        Canvas.SetLeft(el, p.X - el.Width / 2);
        Canvas.SetTop(el, p.Y - el.Height / 2);
        Stage.Children.Add(el);
    }

    private Point EdgeAnchor(EntryEdge edge, double frac) => edge switch
    {
        EntryEdge.Right => new Point(W - 2, frac * H),
        EntryEdge.Left => new Point(2, frac * H),
        EntryEdge.Top => new Point(frac * W, 2),
        _ => new Point(frac * W, H - 2),
    };

    private (double, double) StartOffset(EntryEdge edge, double frac, double lw, double lh)
    {
        double offX = W * 0.6 + lw, offY = H * 0.6 + lh;
        double alongY = (frac - 0.5) * H * 0.55;
        double alongX = (frac - 0.5) * W * 0.55;
        return edge switch
        {
            EntryEdge.Right => (offX, alongY),
            EntryEdge.Left => (-offX, alongY),
            EntryEdge.Top => (alongX, -offY),
            _ => (alongX, offY),
        };
    }

    private static void Animate(DependencyObject target, DependencyProperty prop,
        double from, double to, double ms, IEasingFunction? ease, double beginMs = 0)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(1, ms)))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        if (beginMs > 0) anim.BeginTime = TimeSpan.FromMilliseconds(beginMs);
        ((IAnimatable)target).BeginAnimation(prop, anim);
    }

    private static BitmapImage? LoadBitmap(string path, int decodeHeight)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelHeight = Math.Max(1, decodeHeight);
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
