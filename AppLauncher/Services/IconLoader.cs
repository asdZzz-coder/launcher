using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppLauncher.Services;

/// <summary>從 exe 取出圖示（64px，比 ExtractIcon 的 32px 清楚）。</summary>
public static partial class IconLoader
{
    public static ImageSource? FromExe(string exePath, int size = 64)
    {
        try
        {
            var hr = SHDefExtractIcon(exePath, 0, 0, out var large, out var small, (uint)(size | (16 << 16)));
            if (small != IntPtr.Zero) DestroyIcon(small);
            if (hr != 0 || large == IntPtr.Zero) return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                DestroyIcon(large);
            }
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            return null;
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
