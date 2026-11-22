using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FlipDrop.App;

internal static class Native
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Make a window fully click-through and non-activating (for the fling overlay).</summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>Keep a window out of Alt+Tab / taskbar without making it click-through (for the toast).</summary>
    public static void MakeToolWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }
}

/// <summary>
/// Thumbnails from the Windows Shell (<c>IShellItemImageFactory::GetImage</c>) for files WPF can't
/// decode itself — video, PDF, Office. Runs on a background STA thread with a 2 s timeout, and is
/// never called for risky files. The returned <see cref="BitmapSource"/> is frozen for cross-thread use.
/// </summary>
internal static class ShellThumbnails
{
    private const int SIIGBF_RESIZETOFIT = 0x00000000;
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const int SIIGBF_THUMBNAILONLY = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Returns a shell thumbnail (frozen) or null on timeout/failure. Safe to await from the UI thread;
    /// the shell work runs off it. Caller must guarantee the file is not a risky type.
    /// </summary>
    public static async Task<BitmapSource?> TryGetAsync(string path, int px)
    {
        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            BitmapSource? result = null;
            try { result = GetImage(path, px); }
            catch { /* unsupported / in use — fall back to a vector icon */ }
            tcs.TrySetResult(result);
        })
        { IsBackground = true, Name = "FlipDrop-ShellThumb" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000)).ConfigureAwait(false);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    private static BitmapSource? GetImage(string path, int px)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj);
        var factory = (IShellItemImageFactory)obj;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_BIGGERSIZEOK, out hbitmap);
            if (hbitmap == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(factory);
        }
    }
}

/// <summary>Decodes an image file to a frozen <see cref="BitmapImage"/> off the UI thread.</summary>
internal static class ImageDecoder
{
    public static Task<BitmapImage?> DecodeAsync(string path, int decodePixelWidth) =>
        Task.Run(() =>
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.DecodePixelWidth = Math.Max(1, decodePixelWidth);
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return (BitmapImage?)bi;
            }
            catch { return null; }
        });
}
