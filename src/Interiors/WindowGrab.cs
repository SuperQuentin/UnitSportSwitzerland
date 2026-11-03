using System.Runtime.InteropServices;

namespace UnitSport.Interiors;

/// <summary>
/// One window's pixels for a share (#1298 phase 3, Windows): <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c>
/// (Windows 8.1+), which asks the window to draw itself, GPU content included, into a DIB section: a browser or a
/// player shows even under other windows (this game's, full screen). Not a minimised one.
/// </summary>
internal static class WindowGrab
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    /// <summary>
    /// The window's client area into <paramref name="pixels"/> (RGBA, top row first, grown as needed); false once the
    /// window is gone or minimised.
    /// </summary>
    public static bool Grab(IntPtr window, ref byte[] pixels, out int width, out int height)
    {
        width = height = 0;
        if (!IsWindow(window) || IsIconic(window) || !GetClientRect(window, out var r)) return false;
        width = r.Right - r.Left;
        height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) return true;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        // top-down 32-bit: a negative height
        var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        try
        {
            // PW_CLIENTONLY | PW_RENDERFULLCONTENT
            PrintWindow(window, dc, 1 | 2);
            int bytes = width * height * 4;
            if (pixels.Length < bytes) pixels = new byte[bytes];
            Marshal.Copy(bits, pixels, 0, bytes);
            // BGRA to RGBA, opaque
            for (int i = 0; i < bytes; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                pixels[i + 3] = 255;
            }
            return true;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
