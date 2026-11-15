using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using FlipDrop.Core;

namespace FlipDrop.App;

/// <summary>Renders a colourful sample photo so the Test button can play an effect without a phone.</summary>
internal static class SampleImage
{
    public static string CreateSampleFile()
    {
        AppPaths.EnsureDataDir();
        var path = Path.Combine(AppPaths.DataDir, "sample.jpg");

        using var bmp = new Bitmap(1280, 854, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new LinearGradientBrush(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                Color.FromArgb(255, 44, 62, 120),
                Color.FromArgb(255, 180, 70, 150),
                35f);
            g.FillRectangle(bg, 0, 0, bmp.Width, bmp.Height);

            var rng = new Random(7);
            for (int i = 0; i < 90; i++)
            {
                int r = rng.Next(20, 160);
                int x = rng.Next(0, bmp.Width);
                int y = rng.Next(0, bmp.Height);
                int a = rng.Next(14, 48);
                using var circle = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
                g.FillEllipse(circle, x - r, y - r, r * 2, r * 2);
            }

            using var title = new Font("Segoe UI", 72, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sub = new Font("Segoe UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
            using var tb = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("FlipDrop", title, tb, new RectangleF(0, bmp.Height * 0.40f, bmp.Width, 110), sf);
            g.DrawString("test fling", sub, tb, new RectangleF(0, bmp.Height * 0.55f, bmp.Width, 60), sf);
        }

        bmp.Save(path, ImageFormat.Jpeg);
        return path;
    }
}
