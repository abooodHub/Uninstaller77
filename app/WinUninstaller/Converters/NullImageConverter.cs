using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinUninstaller.Converters;

/// <summary>Returns a generated placeholder ImageSource when the value is null.</summary>
public class NullImageConverter : IValueConverter
{
    private static ImageSource? _placeholder;

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ImageSource img) return img;
        return _placeholder ??= GeneratePlaceholder();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static ImageSource GeneratePlaceholder()
    {
        // 48x48 grey rounded square
        const int size = 48;
        var bmp = new WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            int x = i % size, y = i / size;
            // simple rounded corners: skip corners
            bool isCorner = (x < 8 && y < 8) || (x >= 40 && y < 8) ||
                            (x < 8 && y >= 40) || (x >= 40 && y >= 40);
            if (!isCorner)
            {
                pixels[i * 4 + 0] = 0x6E; // B
                pixels[i * 4 + 1] = 0x6E; // G
                pixels[i * 4 + 2] = 0x6E; // R
                pixels[i * 4 + 3] = 0xFF; // A
            }
        }
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bmp.Freeze();
        return bmp;
    }
}
