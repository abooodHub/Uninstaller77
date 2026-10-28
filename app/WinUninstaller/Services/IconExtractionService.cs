using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinUninstaller.Services;

/// <summary>
/// High-resolution icon extraction service using Win32 PrivateExtractIconsW and Shell32 APIs.
/// Extracts 64x64, 128x128, and 256x256 icons when available in PE/ICO sources.
/// Includes graceful modern stylized badge generation as a foolproof fallback.
/// All returned ImageSource objects are frozen for cross-thread UI rendering.
/// </summary>
public class IconExtractionService
{
    #region Win32 P/Invoke

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        int[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int ExtractIconEx(
        string lpszFile, int nIconIndex,
        IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    #endregion

    /// <summary>
    /// Parses DisplayIcon registry string and extracts the highest-resolution icon possible.
    /// Handles quoted paths, environment variables, icon indices (positive and negative),
    /// and unquoted paths containing parameters.
    /// </summary>
    public ImageSource? ExtractFromDisplayIcon(string displayIcon, string fallbackAppName = "")
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return !string.IsNullOrEmpty(fallbackAppName) ? GenerateAppAvatar(fallbackAppName) : null;

        try
        {
            ParseDisplayIcon(displayIcon, out var path, out var index);
            path = Environment.ExpandEnvironmentVariables(path.Trim('"', ' '));

            if (!File.Exists(path))
            {
                // Unquoted path with arguments: e.g. C:\path\app.exe /arg
                int exeIdx = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx > 0 && File.Exists(path[..(exeIdx + 4)]))
                {
                    path = path[..(exeIdx + 4)];
                }
                else
                {
                    int icoIdx = path.IndexOf(".ico", StringComparison.OrdinalIgnoreCase);
                    if (icoIdx > 0 && File.Exists(path[..(icoIdx + 4)]))
                    {
                        path = path[..(icoIdx + 4)];
                    }
                    else
                    {
                        return !string.IsNullOrEmpty(fallbackAppName) ? GenerateAppAvatar(fallbackAppName) : null;
                    }
                }
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            ImageSource? result = ext switch
            {
                ".ico" => LoadIco(path),
                ".png" or ".jpg" or ".jpeg" or ".bmp" => LoadBitmap(path),
                ".exe" or ".dll" => ExtractHighResFromPe(path, index),
                _ => ExtractHighResFromPe(path, index) ?? ShellIcon(path)
            };

            return result ?? (!string.IsNullOrEmpty(fallbackAppName) ? GenerateAppAvatar(fallbackAppName) : null);
        }
        catch
        {
            return !string.IsNullOrEmpty(fallbackAppName) ? GenerateAppAvatar(fallbackAppName) : null;
        }
    }

    /// <summary>
    /// Extract high-resolution icon from executable or DLL using PrivateExtractIconsW.
    /// Requests 128x128 first, then falls back to 64x64 or standard 32x32.
    /// </summary>
    public ImageSource? ExtractFromExecutable(string exePath, int iconIndex = 0)
    {
        if (!File.Exists(exePath)) return null;
        return ExtractHighResFromPe(exePath, iconIndex) ?? ShellIcon(exePath);
    }

    /// <summary>
    /// Shell32 SHGetFileInfo fallback for files or folders.
    /// </summary>
    public ImageSource? ShellIcon(string path)
    {
        try
        {
            var sfi = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_LARGEICON;
            if (!File.Exists(path) && !Directory.Exists(path))
                flags |= SHGFI_USEFILEATTRIBUTES;

            var result = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref sfi, (uint)Marshal.SizeOf(sfi), flags);
            if (result == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;

            try
            {
                return HIconToImageSource(sfi.hIcon);
            }
            finally
            {
                DestroyIcon(sfi.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Uses PrivateExtractIconsW to retrieve 128x128 or 64x64 high-resolution icons directly from PE resources.
    /// </summary>
    private static ImageSource? ExtractHighResFromPe(string filePath, int iconIndex)
    {
        int[] targetSizes = [128, 64, 48, 32];
        foreach (var size in targetSizes)
        {
            var hIcons = new IntPtr[1];
            var iconIds = new int[1];

            uint count = PrivateExtractIcons(filePath, iconIndex, size, size, hIcons, iconIds, 1, 0);
            if (count > 0 && hIcons[0] != IntPtr.Zero)
            {
                try
                {
                    var img = HIconToImageSource(hIcons[0]);
                    if (img != null) return img;
                }
                finally
                {
                    DestroyIcon(hIcons[0]);
                }
            }
        }

        // Secondary fallback: ExtractIconEx
        try
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            int count = ExtractIconEx(filePath, iconIndex, large, small, 1);
            if (count <= 0 && iconIndex != 0)
            {
                count = ExtractIconEx(filePath, 0, large, small, 1);
            }

            IntPtr hIcon = large[0] != IntPtr.Zero ? large[0] : (small[0] != IntPtr.Zero ? small[0] : IntPtr.Zero);
            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    return HIconToImageSource(hIcon);
                }
                finally
                {
                    if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                    if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Loads an ICO file and selects the frame with the highest resolution.
    /// </summary>
    public static ImageSource? LoadIco(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var frame in decoder.Frames)
            {
                if (best == null || frame.PixelWidth > best.PixelWidth)
                    best = frame;
            }
            if (best == null) return null;
            best.Freeze();
            return BitmapEncodingService.DetachPixels(best);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads a standard bitmap image (PNG/JPG/BMP) safely with cache option OnLoad.
    /// </summary>
    public static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a native HICON to a fully managed WPF BitmapSource with no remaining native dependencies.
    /// </summary>
    private static ImageSource? HIconToImageSource(IntPtr hIcon)
    {
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            int width = bs.PixelWidth;
            int height = bs.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                bs.Freeze();
                return bs;
            }

            var format = PixelFormats.Bgra32;
            var formatted = new FormatConvertedBitmap(bs, format, null, 0);
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            formatted.CopyPixels(pixels, stride, 0);

            var managedBitmap = BitmapSource.Create(
                width, height,
                bs.DpiX, bs.DpiY,
                format,
                null, pixels, stride);
            managedBitmap.Freeze();
            return managedBitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a modern vector avatar badge for applications that don't provide an icon.
    /// Uses a harmonic palette (Catppuccin) determined deterministically by app name hash.
    /// </summary>
    public static ImageSource GenerateAppAvatar(string appName, int size = 128)
    {
        var initials = GetInitials(appName);
        var (bgColor, fgColor) = GetPaletteForName(appName);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Rounded background rectangle
            var brush = new SolidColorBrush(bgColor);
            brush.Freeze();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1.5);
            pen.Freeze();
            dc.DrawRoundedRectangle(brush, pen, new Rect(2, 2, size - 4, size - 4), size * 0.22, size * 0.22);

            // Centered initials
            var textBrush = new SolidColorBrush(fgColor);
            textBrush.Freeze();
            var typeface = new Typeface(new FontFamily("Segoe UI, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var formattedText = new FormattedText(
                initials,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * 0.40,
                textBrush,
                1.0);

            double x = (size - formattedText.Width) / 2.0;
            double y = (size - formattedText.Height) / 2.0;
            dc.DrawText(formattedText, new Point(x, y));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "APP";
        var parts = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var first = parts[0][0];
            var second = parts[1][0];
            return $"{char.ToUpperInvariant(first)}{char.ToUpperInvariant(second)}";
        }
        return name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();
    }

    private static (Color bg, Color fg) GetPaletteForName(string name)
    {
        // Dark theme harmonic colors (Catppuccin Macchiato/Mocha accents)
        Color[] bgColors =
        [
            Color.FromRgb(0x89, 0xB4, 0xFA), // Blue
            Color.FromRgb(0xC6, 0xA0, 0xF6), // Mauve
            Color.FromRgb(0x8A, 0xD4, 0xE6), // Sky
            Color.FromRgb(0xA6, 0xDA, 0x95), // Green
            Color.FromRgb(0xEE, 0x99, 0xA0), // Maroon
            Color.FromRgb(0xF5, 0xA9, 0x7F), // Peach
            Color.FromRgb(0xB7, 0xBD, 0xF8), // Lavender
            Color.FromRgb(0x81, 0xC8, 0xBE), // Teal
        ];

        uint hash = 5381;
        foreach (char c in name)
        {
            hash = ((hash << 5) + hash) + c;
        }

        var bg = bgColors[hash % bgColors.Length];
        var fg = Color.FromRgb(0x18, 0x18, 0x25); // Dark mantle text for high contrast
        return (bg, fg);
    }

    private static void ParseDisplayIcon(string raw, out string path, out int index)
    {
        index = 0;
        raw = raw.Trim();

        // Quoted path: "C:\path\file.exe",N
        if (raw.StartsWith('"'))
        {
            var endQuote = raw.IndexOf('"', 1);
            if (endQuote > 0)
            {
                path = raw[1..endQuote];
                var rest = raw[(endQuote + 1)..].Trim(',', ' ');
                if (!string.IsNullOrEmpty(rest) && int.TryParse(rest, out var idx))
                    index = idx;
                return;
            }
        }

        // Un-quoted path with comma: C:\path\file.exe,N
        var comma = raw.LastIndexOf(',');
        if (comma > 0 && comma < raw.Length - 1)
        {
            var suffix = raw[(comma + 1)..].Trim();
            if (int.TryParse(suffix, out var idx))
            {
                path = raw[..comma].Trim();
                index = idx;
                return;
            }
        }

        path = raw;
    }
}
