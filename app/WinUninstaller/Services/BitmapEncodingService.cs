using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinUninstaller.Services;

/// <summary>Copy pixels, not a decoder's thread-affine metadata, across the UI boundary.</summary>
public static class BitmapEncodingService
{
    public static BitmapSource DetachPixels(BitmapSource source)
    {
        var width = source.PixelWidth; var height = source.PixelHeight;
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new InvalidDataException("Unsupported icon dimensions.");
        BitmapSource formatted = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
        formatted.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        detached.Freeze();
        return detached;
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(DetachPixels(source)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
