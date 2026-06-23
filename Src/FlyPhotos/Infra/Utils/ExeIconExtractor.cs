#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Xaml.Media;
using NLog;

namespace FlyPhotos.Infra.Utils;

/// <summary>
/// Extracts the icon embedded in a Win32 executable and turns it into a WinUI
/// <see cref="ImageSource"/>, using GDI directly instead of System.Drawing.
/// </summary>
internal static class ExeIconExtractor
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Extracts an icon from an executable file and converts it to an <see cref="ImageSource"/>.
    /// </summary>
    /// <param name="exePath">The path to the executable.</param>
    /// <returns>A task returning the <see cref="ImageSource"/> or null if extraction fails.</returns>
    public static async Task<ImageSource?> ExtractAsync(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;

        // Rasterise the icon to a BGRA8 buffer off the UI thread, then build the
        // WriteableBitmap on the calling (UI) thread where the await resumes.
        (int Width, int Height, byte[] Pixels)? icon = await Task.Run(() =>
        {
            try
            {
                return ExtractIconPixels(exePath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ExeIconExtractor Error");
                return null;
            }
        });

        if (icon == null) return null;

        var (width, height, pixels) = icon.Value;
        return Util.CreateBitmapFromBgra(width, height, pixels);
    }

    /// <summary>
    /// Extracts the first icon from an executable and rasterises it to a premultiplied
    /// BGRA8 pixel buffer using GDI. Replaces System.Drawing's icon handling.
    /// </summary>
    private static (int Width, int Height, byte[] Pixels)? ExtractIconPixels(string exePath)
    {
        if (Win32Methods.ExtractIconEx(exePath, 0, out IntPtr hLarge, out IntPtr hSmall, 1) <= 0)
            return null;

        // Prefer the large (typically 32x32) icon; fall back to the small one.
        IntPtr hIcon = hLarge != IntPtr.Zero ? hLarge : hSmall;
        IntPtr unused = hLarge != IntPtr.Zero ? hSmall : hLarge;
        if (unused != IntPtr.Zero) Win32Methods.DestroyIcon(unused);
        if (hIcon == IntPtr.Zero) return null;

        IntPtr hdc = IntPtr.Zero;
        try
        {
            if (!Win32Methods.GetIconInfo(hIcon, out var iconInfo))
                return null;

            try
            {
                var bmp = new Win32Methods.BITMAP();
                if (Win32Methods.GetObject(iconInfo.hbmColor, Marshal.SizeOf<Win32Methods.BITMAP>(), ref bmp) == 0)
                    return null;

                int width = bmp.bmWidth;
                int height = bmp.bmHeight;
                if (width <= 0 || height <= 0) return null;

                var header = new Win32Methods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32Methods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // negative => top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32Methods.BI_RGB
                };

                var pixels = new byte[width * height * 4];
                hdc = Win32Methods.CreateCompatibleDC(IntPtr.Zero);
                int scanLines = Win32Methods.GetDIBits(hdc, iconInfo.hbmColor, 0, (uint)height,
                    pixels, ref header, Win32Methods.DIB_RGB_COLORS);
                if (scanLines == 0) return null;

                NormalizeIconAlpha(pixels);
                return (width, height, pixels);
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero) Win32Methods.DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) Win32Methods.DeleteObject(iconInfo.hbmMask);
            }
        }
        finally
        {
            if (hdc != IntPtr.Zero) Win32Methods.DeleteDC(hdc);
            Win32Methods.DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Fixes up the alpha channel of a BGRA icon buffer in place: legacy icons with no alpha
    /// information come back fully transparent, so they are forced opaque; otherwise the
    /// straight-alpha pixels are premultiplied to match <see cref="Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap"/>'s format.
    /// </summary>
    private static void NormalizeIconAlpha(byte[] bgra)
    {
        bool hasAlpha = false;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) { hasAlpha = true; break; }
        }

        if (!hasAlpha)
        {
            for (int i = 3; i < bgra.Length; i += 4)
                bgra[i] = 255;
            return;
        }

        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
        }
    }
}
