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
/// A single, reused full-screen, transparent, click-through, topmost overlay that plays fling
/// landing animations. Each arrival is an independent "group" of visuals that cleans itself up,
/// so a new file never waits on the previous animation. Bursts beyond <see cref="MaxConcurrent"/>
/// are coalesced to the lighter "subtle" treatment. The matching corner toast is shown separately
/// and immediately by the caller.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int MaxConcurrent = 4;

    private double W, H;
    private int _activeGroups;
    private bool _shownOnce;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.MakeClickThrough(this);
    }

    /// <summary>Fire-and-forget. Plays an arrival without blocking the caller or the next file.</summary>
    public void Play(FileReceivedEventArgs item, string effect, EntryEdge edge, string animation)
    {
        if (string.Equals(animation, "Off", StringComparison.OrdinalIgnoreCase)) return;
        bool subtle = string.Equals(animation, "Subtle", StringComparison.OrdinalIgnoreCase)
                      || _activeGroups >= MaxConcurrent;
        _ = PlayGroupAsync(item, effect, edge, subtle);
    }

    private async Task PlayGroupAsync(FileReceivedEventArgs item, string effect, EntryEdge edge, bool subtle)
    {
        _activeGroups++;
        var bucket = new List<UIElement>();
        try
        {
            EnsureStage();

            FrameworkElement subject;
            double landedW, landedH;

            BitmapImage? bmp = item.Kind == FileKind.Image && !item.IsRisky
                ? await ImageDecoder.DecodeAsync(item.FilePath, (int)Math.Ceiling(W * 0.6))
                : null;

            if (bmp is not null)
            {
                double aspect = (double)bmp.PixelWidth / bmp.PixelHeight;
                landedH = Math.Min(H * 0.55, bmp.PixelHeight);
                landedW = landedH * aspect;
                if (landedW > W * 0.82) { landedW = W * 0.82; landedH = landedW / aspect; }
                subject = BuildPhotoCard(bmp, landedW, landedH);
            }
            else
            {
                landedW = 380; landedH = 228;
                subject = BuildFileCard(item, landedW, landedH);
            }

            await RunSubjectAsync(subject, landedW, landedH, item, effect, edge, subtle, bucket);
        }
        catch { /* never let one animation break the receiver */ }
        finally
        {
            foreach (var el in bucket) Stage.Children.Remove(el);
            _activeGroups--;
            if (_activeGroups <= 0 && Stage.Children.Count == 0) Hide();
        }
    }

    private void EnsureStage()
    {
        W = SystemParameters.PrimaryScreenWidth;
        H = SystemParameters.PrimaryScreenHeight;
        Left = 0; Top = 0; Width = W; Height = H;
        Stage.Width = W; Stage.Height = H;
        if (!IsVisible) Show();
        if (!_shownOnce) { _shownOnce = true; Native.MakeClickThrough(this); }
        Topmost = true;
    }

    private async Task RunSubjectAsync(FrameworkElement subject, double landedW, double landedH,
        FileReceivedEventArgs item, string effect, EntryEdge edge, bool subtle, List<UIElement> bucket)
    {
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(translate);
        subject.RenderTransform = group;
        subject.RenderTransformOrigin = new Point(0.5, 0.5);
        subject.Opacity = 0;
        Canvas.SetLeft(subject, (W - landedW) / 2);
        Canvas.SetTop(subject, (H - landedH) / 2);

        double frac = Math.Clamp(item.FlingX ?? 0.5, 0, 1);
        Point anchor = EdgeAnchor(edge, frac);
        (double sx, double sy) = StartOffset(edge, frac, landedW, landedH);

        double vel = item.FlingVelocity ?? 2000;
        double entryMs = Math.Clamp(900 - vel / 9.0, subtle ? 300 : 360, subtle ? 620 : 860);
        var center = new Point(W / 2, H / 2);

        bool ripple = string.Equals(effect, "Ripple", StringComparison.OrdinalIgnoreCase);

        // Decorations (behind the subject). Subtle skips the big full-screen sweep.
        if (ripple)
        {
            // Ripple lands in place; rings expand on arrival (added below after entry).
            sx = 0; sy = 0;
        }
        else if (!subtle)
        {
            switch (effect)
            {
                case "Portal": BuildPortal(anchor, entryMs, bucket); break;
                case "Comet": BuildComet(anchor, center, entryMs, bucket); break;
                default: BuildRainbow(anchor, entryMs, bucket); break;
            }
        }
        else if (effect == "Comet")
        {
            BuildComet(anchor, center, entryMs, bucket, trailOnly: true);
        }

        Add(subject, bucket);

        double startScale = ripple ? 0.72
            : effect == "Portal" ? 0.2
            : effect == "Comet" ? 0.36 : 0.56;
        double startAngle = ripple ? 0 : effect == "Comet" ? -12 : effect == "Portal" ? 18 : -5;
        scale.ScaleX = scale.ScaleY = startScale;
        rotate.Angle = startAngle;
        translate.X = sx; translate.Y = sy;

        IEasingFunction entryEase = effect == "Comet" && !subtle
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        Animate(subject, OpacityProperty, 0, 1, Math.Min(entryMs * 0.6, 380), new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(translate, TranslateTransform.XProperty, sx, 0, entryMs, entryEase);
        Animate(translate, TranslateTransform.YProperty, sy, 0, entryMs, entryEase);
        Animate(scale, ScaleTransform.ScaleXProperty, startScale, 1, entryMs, entryEase);
        Animate(scale, ScaleTransform.ScaleYProperty, startScale, 1, entryMs, entryEase);
        Animate(rotate, RotateTransform.AngleProperty, startAngle, 0, entryMs, new CubicEase { EasingMode = EasingMode.EaseOut });

        if (ripple) BuildRipple(center, subtle ? 620 : 900, bucket, beginMs: entryMs * 0.55);

        await Task.Delay((int)entryMs + 40);

        // Hold ~0.5 s (full) / ~0.3 s (subtle).
        await Task.Delay(subtle ? 300 : 500);

        // Glide to the bottom-right toast corner as a shrinking thumbnail.
        var wa = SystemParameters.WorkArea;
        const double toastW = 340, toastH = 150, margin = 14;
        double cornerCx = wa.Right - margin - toastW / 2;
        double cornerCy = wa.Bottom - margin - toastH / 2;
        double exitScale = Math.Min((toastW - 24) / landedW, (toastH - 24) / landedH);
        double exitMs = subtle ? 360 : 460;
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

        Animate(translate, TranslateTransform.XProperty, 0, cornerCx - W / 2, exitMs, exitEase);
        Animate(translate, TranslateTransform.YProperty, 0, cornerCy - H / 2, exitMs, exitEase);
        Animate(scale, ScaleTransform.ScaleXProperty, 1, exitScale, exitMs, exitEase);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, exitScale, exitMs, exitEase);
        Animate(subject, OpacityProperty, 1, 0, 180, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: exitMs - 150);

        await Task.Delay((int)exitMs + 50);
    }

    // ---------- subjects ----------

    private static Border BuildPhotoCard(BitmapImage bmp, double w, double h) => new()
    {
        Width = w,
        Height = h,
        CornerRadius = new CornerRadius(18),
        Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
        ClipToBounds = true,
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 46, ShadowDepth = 14, Opacity = 0.5, Color = Colors.Black, Direction = 270 }
    };

    private static Border BuildFileCard(FileReceivedEventArgs item, double w, double h)
    {
        var accent = item.IsRisky ? FileIcons.RiskyAccent : FileIcons.Accent(item.Kind);

        var edgeGlow = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xFF, 0x8A, 0x3D), 0),
                new GradientStop(accent, 0.55),
                new GradientStop(Color.FromRgb(0x8A, 0x5B, 0xE0), 1),
            }
        };

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26) };
        content.Children.Add(FileIcons.Build(item.Kind, item.IsRisky, 68, new SolidColorBrush(accent)));
        content.Children.Add(new TextBlock
        {
            Text = item.FileName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxWidth = w - 52,
            Margin = new Thickness(0, 16, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
        });
        content.Children.Add(new TextBlock
        {
            Text = item.IsRisky
                ? $"{FileIcons.Label(item.Kind)} · {FileTypes.FormatSize(item.SizeBytes)} · not opened"
                : $"{FileIcons.Label(item.Kind)} · {FileTypes.FormatSize(item.SizeBytes)}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
        });

        var inner = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x24, 0x27, 0x33), Color.FromRgb(0x17, 0x18, 0x22), 90),
            Child = content
        };

        return new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(19),
            Padding = new Thickness(3),
            Background = edgeGlow,
            Child = inner,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 50, ShadowDepth = 12, Opacity = 0.55, Color = accent, Direction = 270 }
        };
    }

    // ---------- effect decorations ----------

    private void BuildRipple(Point center, double ms, List<UIElement> bucket, double beginMs)
    {
        var colors = new[]
        {
            Color.FromRgb(0xFF, 0x8A, 0x3D),
            Color.FromRgb(0xE2, 0x4A, 0xA0),
            Color.FromRgb(0x8A, 0x5B, 0xE0),
        };
        for (int i = 0; i < 3; i++)
        {
            const double baseD = 220;
            var ring = new Ellipse
            {
                Width = baseD,
                Height = baseD,
                Opacity = 0,
                Stroke = new SolidColorBrush(colors[i % colors.Length]),
                StrokeThickness = 6 - i,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 5 }
            };
            var rs = new ScaleTransform(0.25, 0.25);
            ring.RenderTransform = rs;
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            AddCentered(ring, center, bucket);

            double begin = beginMs + i * 130;
            Animate(ring, OpacityProperty, 0, 0.85, ms * 0.25, new CubicEase(), beginMs: begin);
            Animate(ring, OpacityProperty, 0.85, 0, ms * 0.8, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: begin + ms * 0.2);
            Animate(rs, ScaleTransform.ScaleXProperty, 0.25, 3.4, ms, new CubicEase { EasingMode = EasingMode.EaseOut }, beginMs: begin);
            Animate(rs, ScaleTransform.ScaleYProperty, 0.25, 3.4, ms, new CubicEase { EasingMode = EasingMode.EaseOut }, beginMs: begin);
        }
    }

    private void BuildRainbow(Point anchor, double entryMs, List<UIElement> bucket)
    {
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
        Add(blob, bucket);

        double towardX = (W / 2 - anchor.X) * 0.55;
        double towardY = (H / 2 - anchor.Y) * 0.55;
        Animate(blob, OpacityProperty, 0, 0.95, entryMs * 0.45, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(blob, OpacityProperty, 0.95, 0, entryMs * 0.9, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.5);
        Animate(t, TranslateTransform.XProperty, 0, towardX, entryMs * 1.3, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(t, TranslateTransform.YProperty, 0, towardY, entryMs * 1.3, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    private void BuildPortal(Point anchor, double entryMs, List<UIElement> bucket)
    {
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
        AddCentered(glow, anchor, bucket);
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
            AddCentered(ring, anchor, bucket);

            double begin = i * 55;
            Animate(ring, OpacityProperty, 0, 0.9, 220, new CubicEase(), beginMs: begin);
            Animate(ring, OpacityProperty, 0.9, 0.0, entryMs, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.65 + begin);
            Animate(rs, ScaleTransform.ScaleXProperty, 0.2, 1.25, entryMs + begin, new CubicEase { EasingMode = EasingMode.EaseOut }, beginMs: begin);
            Animate(rs, ScaleTransform.ScaleYProperty, 0.2, 1.25, entryMs + begin, new CubicEase { EasingMode = EasingMode.EaseOut }, beginMs: begin);
        }

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
            Add(star, bucket);
            var tw = new DoubleAnimation(0.1, 0.9, TimeSpan.FromMilliseconds(500 + rng.Next(600)))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3),
                BeginTime = TimeSpan.FromMilliseconds(rng.Next(400))
            };
            star.BeginAnimation(OpacityProperty, tw);
        }

        BuildComet(anchor, new Point(W / 2, H / 2), entryMs, bucket, trailOnly: true);
    }

    private void BuildComet(Point anchor, Point target, double entryMs, List<UIElement> bucket, bool trailOnly = false)
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
        Add(trail, bucket);

        double peak = trailOnly ? 0.5 : 0.85;
        Animate(trail, OpacityProperty, 0, peak, entryMs * 0.35, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(trail, OpacityProperty, peak, 0, entryMs * 0.9, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.4);

        if (trailOnly) return;

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
        Add(head, bucket);
        Animate(head, OpacityProperty, 0, 0.95, 160, new CubicEase());
        Animate(head, OpacityProperty, 0.95, 0, 260, new CubicEase { EasingMode = EasingMode.EaseIn }, beginMs: entryMs * 0.55);
        Animate(ht, TranslateTransform.XProperty, 0, dx, entryMs * 0.7, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(ht, TranslateTransform.YProperty, 0, dy, entryMs * 0.7, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    // ---------- helpers ----------

    private void Add(UIElement el, List<UIElement> bucket)
    {
        Stage.Children.Add(el);
        bucket.Add(el);
    }

    private void AddCentered(FrameworkElement el, Point p, List<UIElement> bucket)
    {
        Canvas.SetLeft(el, p.X - el.Width / 2);
        Canvas.SetTop(el, p.Y - el.Height / 2);
        Add(el, bucket);
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
}
