using System.Windows.Media;

namespace WinUninstaller.Views;

public static partial class ProductTheme
{
    internal static System.Drawing.Color WebBackground { get; } = System.Drawing.Color.FromArgb(unchecked((int)0xFF000000) | CanvasRgb);
    internal static int CaptionColor => ColorRef(ChromeRgb);
    internal static int CaptionTextColor => ColorRef(TextRgb);

    public static Brush Canvas { get; } = BrushFromRgb(CanvasRgb);
    public static Brush Surface { get; } = BrushFromRgb(SurfaceRgb);
    public static Brush Text { get; } = BrushFromRgb(TextRgb);
    public static Brush Muted { get; } = BrushFromRgb(MutedRgb);

    private static int ColorRef(int rgb) => (rgb & 0xFF) << 16 | (rgb & 0xFF00) | rgb >> 16;

    private static SolidColorBrush BrushFromRgb(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
