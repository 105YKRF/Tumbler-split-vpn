using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tumbler;

/// <summary>Общее оформление окон.</summary>
internal static class Chrome
{
    public static void UseDarkTitleBar(Window window)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int enabled = 1;
        Native.DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static ImageSource DrawWindowIcon()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(Brush("Accent"), null, new Rect(2, 16, 60, 32), 16, 16);
            dc.DrawEllipse(Brushes.White, null, new Point(44, 32), 11, 11);
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
