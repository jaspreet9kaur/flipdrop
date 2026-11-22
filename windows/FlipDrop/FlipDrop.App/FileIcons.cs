using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FlipDrop.Core;
using Path = System.Windows.Shapes.Path;

namespace FlipDrop.App;

/// <summary>
/// Vector type icons drawn as XAML <see cref="Geometry"/> paths (no raster assets), plus a
/// per-kind accent colour and a short human label. Used by the toast and the landing overlay
/// whenever a file has no thumbnail (every non-image kind, and risky files).
/// </summary>
internal static class FileIcons
{
    // Stroke-outline geometries in a 24x24 box (StrokeLineJoin=Round renders them cleanly).
    private const string Image =
        "M5,5 H19 A1,1 0 0 1 20,6 V18 A1,1 0 0 1 19,19 H5 A1,1 0 0 1 4,18 V6 A1,1 0 0 1 5,5 Z " +
        "M9,10 A1.3,1.3 0 1 1 8.99,10 Z M5,17 L10,12 L13.5,15.5 M12,16 L15.5,12 L20,16.5";
    private const string Video =
        "M5,6 H19 A1,1 0 0 1 20,7 V17 A1,1 0 0 1 19,18 H5 A1,1 0 0 1 4,17 V7 A1,1 0 0 1 5,6 Z " +
        "M10,9.5 L15,12 L10,14.5 Z";
    private const string Audio =
        "M9,18 V7 L18,5 V16 M9,18 A2,2 0 1 1 5,18 A2,2 0 1 1 9,18 Z M18,16 A2,2 0 1 1 14,16 A2,2 0 1 1 18,16 Z";
    private const string Pdf =
        "M7,4 H14 L19,9 V20 H7 Z M14,4 V9 H19 M8.5,17 H15.5 V19.5 H8.5 Z";
    private const string Document =
        "M7,4 H14 L19,9 V20 H7 Z M14,4 V9 H19 M9,13 H16 M9,16 H15 M9,10 H12";
    private const string Text =
        "M7,4 H14 L19,9 V20 H7 Z M14,4 V9 H19 M9,11 H16 M9,14 H16 M9,17 H14";
    private const string Archive =
        "M6,5 H18 V20 H6 Z M11,5 V20 M11,8 H13 M11,11 H13 M11,14 H13";
    private const string Program =
        "M9,6 L4,12 L9,18 M15,6 L20,12 L15,18"; // a code chevron < >
    private const string Warning =
        "M12,4 L21,19 H3 Z M12,9.5 V14 M12,16.4 V16.6";
    private const string Generic =
        "M7,4 H14 L19,9 V20 H7 Z M14,4 V9 H19";

    public static string PathData(FileKind kind) => kind switch
    {
        FileKind.Image => Image,
        FileKind.Video => Video,
        FileKind.Audio => Audio,
        FileKind.Pdf => Pdf,
        FileKind.Document => Document,
        FileKind.Text => Text,
        FileKind.Archive => Archive,
        FileKind.App => Program,
        _ => Generic,
    };

    public static Color Accent(FileKind kind) => kind switch
    {
        FileKind.Image => Color.FromRgb(0xE2, 0x4A, 0xA0),
        FileKind.Video => Color.FromRgb(0x8A, 0x5B, 0xE0),
        FileKind.Audio => Color.FromRgb(0x18, 0xB2, 0xA0),
        FileKind.Pdf => Color.FromRgb(0xE5, 0x48, 0x4D),
        FileKind.Document => Color.FromRgb(0x3B, 0x82, 0xF6),
        FileKind.Text => Color.FromRgb(0x64, 0x74, 0x8B),
        FileKind.Archive => Color.FromRgb(0xF5, 0x9E, 0x0B),
        FileKind.App => Color.FromRgb(0xEF, 0x8A, 0x3D),
        _ => Color.FromRgb(0x8A, 0x8A, 0x8E),
    };

    public static readonly Color RiskyAccent = Color.FromRgb(0xF5, 0x9E, 0x0B);

    public static string Label(FileKind kind) => kind switch
    {
        FileKind.Image => "Image",
        FileKind.Video => "Video",
        FileKind.Audio => "Audio",
        FileKind.Pdf => "PDF",
        FileKind.Document => "Document",
        FileKind.Text => "Text",
        FileKind.Archive => "Archive",
        FileKind.App => "Program",
        _ => "File",
    };

    public static Geometry Geometry(FileKind kind, bool risky)
    {
        var g = System.Windows.Media.Geometry.Parse(risky ? Warning : PathData(kind));
        g.Freeze();
        return g;
    }

    /// <summary>Builds a stroked <see cref="Path"/> inside a <see cref="Viewbox"/> at the requested size.</summary>
    public static FrameworkElement Build(FileKind kind, bool risky, double size, Brush stroke)
    {
        var path = new Path
        {
            Data = Geometry(kind, risky),
            Stroke = stroke,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
        };
        return new Viewbox { Width = size, Height = size, Child = path, Stretch = Stretch.Uniform };
    }
}
