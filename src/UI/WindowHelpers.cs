using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LibraryWarden.UI;

/// <summary>Win32-Hilfen für Fensterrahmen, Maximieren und Fensterposition.</summary>
public static class WindowHelpers
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int SM_CXSIZEFRAME = 32;
    const int SM_CXPADDEDBORDER = 92;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>Dunkler Fensterrahmen/Titelleiste für Fenster mit Standard-Rahmen (und dunkler Rand beim eigenen Rahmen).</summary>
    public static void ApplyDarkFrame(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { }
    }

    /// <summary>Breite des unsichtbaren Fensterrands, der bei maximierten Fenstern über den Bildschirm hinausragt.</summary>
    public static Thickness MaximizedOverhang(Window window)
    {
        try
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            uint dpiValue = (uint)Math.Round(96 * dpi.DpiScaleX);
            int px = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpiValue) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpiValue);
            double dip = px / dpi.DpiScaleX;
            return new Thickness(dip);
        }
        catch
        {
            return new Thickness(8);
        }
    }

    static System.Windows.Media.Imaging.BitmapDecoder? _iconDecoder;

    /// <summary>Passende Einzelgröße aus app.ico (kleine Größen zeigen ein vereinfachtes Motiv).</summary>
    public static System.Windows.Media.ImageSource? IconFrame(double pixels)
    {
        try
        {
            _iconDecoder ??= System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frames = _iconDecoder.Frames.OrderBy(f => f.PixelWidth).ToList();
            return frames.FirstOrDefault(f => f.PixelWidth >= pixels - 0.5) ?? frames.LastOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Gespeicherte Position nur übernehmen, wenn sie (zumindest teilweise) auf einem Bildschirm liegt.</summary>
    public static bool IsOnScreen(double left, double top, double width)
    {
        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return area.IntersectsWith(new Rect(left + 40, top, Math.Max(80, width - 80), 32));
    }
}
