using System.Runtime.InteropServices;
using System.Windows;
using ZXing;
using ZXing.Common;

namespace DynamicIsland;

static class QrReader
{
    const int DesktopLeft = 76, DesktopTop = 77, DesktopWidth = 78, DesktopHeight = 79;
    const int CopyWithLayeredWindows = 0x00CC0020 | 0x40000000;
    const int BytesPerPixel = 4;

    public static Int32Rect Desktop => new(GetSystemMetrics(DesktopLeft), GetSystemMetrics(DesktopTop),
        GetSystemMetrics(DesktopWidth), GetSystemMetrics(DesktopHeight));

    public static string? Read(Int32Rect area)
    {
        int width = area.Width, height = area.Height;
        byte[] pixels = Capture(area.X, area.Y, width, height);
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, TryInverted = true },
        };
        return reader.Decode(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGR32))?.Text;
    }

    internal static byte[] Capture(int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 32_000_000) throw new ArgumentOutOfRangeException(nameof(width));
        byte[] pixels = new byte[checked(width * height * BytesPerPixel)];
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            IntPtr previous = SelectObject(memory, bitmap);
            bool copied = BitBlt(memory, 0, 0, width, height, screen, left, top, CopyWithLayeredWindows);
            SelectObject(memory, previous);
            if (!copied) throw new System.ComponentModel.Win32Exception();
            var info = new BITMAPINFOHEADER
            {
                Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = BytesPerPixel * 8,
            };
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
        return pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr to, int x, int y, int width, int height, IntPtr from, int fromX, int fromY, int operation);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}
