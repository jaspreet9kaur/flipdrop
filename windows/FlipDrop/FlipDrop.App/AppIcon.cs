using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FlipDrop.App;

/// <summary>The tray icon: the app's own icon (flipdrop.ico, built into the .exe), or a drawn fallback.</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                using var big = Icon.ExtractAssociatedIcon(exe);
                if (big is not null) return new Icon(big, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch { /* fall back to the drawn icon */ }
        return Drawn();
    }

    private static Icon Drawn()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var path = Rounded(new Rectangle(2, 2, 28, 28), 8);
            using var grad = new LinearGradientBrush(
                new Rectangle(0, 0, 32, 32),
                Color.FromArgb(255, 255, 138, 61),
                Color.FromArgb(255, 138, 91, 224),
                55f);
            g.FillPath(grad, path);

            // A little dropping "photo" square tilted inside.
            using var inner = new GraphicsPath();
            var pts = new[] { new PointF(12, 9), new PointF(23, 12), new PointF(20, 23), new PointF(9, 20) };
            inner.AddPolygon(pts);
            using var white = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            g.FillPath(white, inner);
        }

        IntPtr hIcon = bmp.GetHicon();
        using var tmp = Icon.FromHandle(hIcon);
        // Clone so we own a managed copy and can free the GDI handle.
        var icon = (Icon)tmp.Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
