using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>
/// Two-level icon cache:
    /// 1. In-memory dictionary (keyed by app ID)
    /// 2. Disk PNG files at %LocalAppData%\WinUninstaller\IconCache\
/// Preserves icons permanently even after the program is uninstalled.
/// </summary>
public class IconCacheService
{
    private readonly string _cacheDir;
    private readonly Dictionary<string, ImageSource> _memCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IconExtractionService _extractor;

    public IconCacheService(IconExtractionService extractor, string? cacheDirectory = null)
    {
        _extractor = extractor;
        _cacheDir = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUninstaller", "IconCache");
        Directory.CreateDirectory(_cacheDir);
    }

    public string CacheDir => _cacheDir;

    /// <summary>
    /// Returns an extracted or cached real application icon. Missing icons stay missing.
    /// </summary>
    public ImageSource? GetIcon(InstalledApp app)
    {
        var key = app.Source == "MSIX" ? app.Id : app.RegistryKeyPath;
        return GetIcon(string.IsNullOrWhiteSpace(key) ? app.Id : key,
            app.Name, app.DisplayIcon, app.InstallLocation, app.UninstallString, app.Source);
    }

    /// <summary>
    /// Returns an extracted or cached real application icon. Missing icons stay missing.
    /// </summary>
    public ImageSource? GetIcon(
        string appId,
        string appName,
        string displayIcon,
        string installLocation,
        string uninstallString,
        string source = "Registry")
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        lock (_memCache)
        {
            if (_memCache.TryGetValue(appId, out var cached))
                return cached;
        }

        // 1. Try a real icon from disk; legacy generated avatars are not app icons.
        var pngPath = Path.Combine(_cacheDir, $"{SanitizeId(appId)}.png");
        if (File.Exists(pngPath) && File.Exists(pngPath + ".origin"))
        {
            var cachedOrigin = ReadOrigin(pngPath);
            var img = string.Equals(cachedOrigin, "Generated avatar", StringComparison.OrdinalIgnoreCase)
                ? null
                : LoadPng(pngPath);
            if (img != null)
            {
                lock (_memCache) _memCache[appId] = img;
                return img;
            }
        }

        // 2. Extract fresh
        var icon = ExtractIcon(appName, displayIcon, installLocation, uninstallString, source, out var origin);

        if (icon != null)
        {
            lock (_memCache) _memCache[appId] = icon;
            // Persist to disk asynchronously
            _ = Task.Run(() =>
            {
                SavePng(icon, pngPath);
                WriteOrigin(pngPath, origin);
            });
        }

        return icon;
    }

    /// <summary>
    /// Persists an icon to disk (call before uninstall to retain icon in history).
    /// Returns the disk path.
    /// </summary>
    public string PersistIcon(string appId, ImageSource? icon, string appName = "")
    {
        var pngPath = Path.Combine(_cacheDir, $"{SanitizeId(appId)}.png");
        if (icon == null && LoadPng(pngPath) != null)
        {
            if (!string.Equals(ReadOrigin(pngPath), "Generated avatar", StringComparison.OrdinalIgnoreCase))
                return pngPath;
            return string.Empty;
        }
        if (icon == null) return string.Empty;

        SavePng(icon, pngPath);
        WriteOrigin(pngPath, "Persisted icon");
        return LoadPng(pngPath) != null ? pngPath : string.Empty;
    }

    /// <summary>
    /// Load a PNG from disk into a frozen BitmapImage.
    /// </summary>
    public static ImageSource? LoadPng(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length is <= 0 or > 4_194_304) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? ExtractIcon(
        string appName,
        string displayIcon,
        string installLocation,
        string uninstallString,
        string source,
        out string origin)
    {
        origin = "Missing";
        // 1. MSIX / AppX Store Package
        if (string.Equals(source, "MSIX", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
        {
            var msixIcon = ExtractMsixLogo(installLocation);
            if (msixIcon != null) { origin = "MSIX asset"; return msixIcon; }
        }

        // 2. Registry DisplayIcon
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var img = _extractor.ExtractFromDisplayIcon(displayIcon);
            if (img != null) { origin = "DisplayIcon"; return img; }
        }

        // 3. Smart Scan of Install Location (find primary executable)
        if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
        {
            var bestExe = FindPrimaryExecutable(installLocation, appName, uninstallString);
            if (!string.IsNullOrEmpty(bestExe))
            {
                var img = _extractor.ExtractFromExecutable(bestExe);
                if (img != null) { origin = "Primary executable"; return img; }
            }
        }

        // 4. Some installers omit InstallLocation but place unins*.exe beside the app.
        if (string.IsNullOrWhiteSpace(installLocation) && !string.IsNullOrEmpty(uninstallString))
        {
            var uninstaller = ParseExeFromString(uninstallString);
            var parent = !string.IsNullOrWhiteSpace(uninstaller) ? Path.GetDirectoryName(uninstaller) : null;
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) &&
                !string.Equals(parent, Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var primary = FindPrimaryExecutable(parent, appName, uninstallString);
                if (primary != null)
                {
                    var img = _extractor.ExtractFromExecutable(primary);
                    if (img != null) { origin = "Primary executable beside uninstaller"; return img; }
                }
            }
        }

        // 5. Executable from UninstallString
        if (!string.IsNullOrEmpty(uninstallString))
        {
            var exePath = ParseExeFromString(uninstallString);
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var img = _extractor.ExtractFromExecutable(exePath);
                if (img != null) { origin = "Uninstaller executable"; return img; }
            }
        }

        // 6. MSI Windows Installer Cache: C:\Windows\Installer\{GUID}\ (READ-ONLY)
        if (!string.IsNullOrEmpty(uninstallString))
        {
            var match = Regex.Match(
                uninstallString,
                @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
            if (match.Success)
            {
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var msiDir = Path.Combine(winDir, "Installer", match.Value);
                if (Directory.Exists(msiDir))
                {
                    try
                    {
                        var ico = Directory.GetFiles(msiDir, "*.ico", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (ico != null)
                        {
                            var img = IconExtractionService.LoadIco(ico);
                            if (img != null) { origin = "MSI cache ICO"; return img; }
                        }

                        var exe = Directory.GetFiles(msiDir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (exe != null)
                        {
                            var img = _extractor.ExtractFromExecutable(exe);
                            if (img != null) { origin = "MSI cache executable"; return img; }
                        }
                    }
                    catch { }
                }
            }
        }

        return null;
    }

    public (string Origin, int Width, int Height, string Hash) AuditIcon(InstalledApp app)
    {
        var icon = ExtractIcon(app.Name, app.DisplayIcon, app.InstallLocation,
            app.UninstallString, app.Source, out var origin);
        if (icon is BitmapSource bitmap)
        {
            var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return (origin, bitmap.PixelWidth, bitmap.PixelHeight,
                Convert.ToHexString(SHA256.HashData(pixels)));
        }
        return ("Missing", 0, 0, string.Empty);
    }

    /// <summary>
    /// Intelligently finds the primary executable without picking uninstaller or helper exes.
    /// </summary>
    private static string? FindPrimaryExecutable(string folder, string appName, string uninstallString)
    {
        try
        {
            var exes = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
            if (exes.Length == 0)
            {
                // check subdirectories 1 level deep (e.g. bin, app)
                exes = Directory.EnumerateDirectories(folder)
                    .Take(16)
                    .SelectMany(dir => Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(8))
                    .Take(64).ToArray();
            }

            if (exes.Length == 0) return null;

            string folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
            string cleanAppName = new(appName.Where(char.IsLetterOrDigit).ToArray());

            var ranked = exes
                .Select(exe =>
                {
                    string fileName = Path.GetFileNameWithoutExtension(exe);
                    string cleanFile = new(fileName.Where(char.IsLetterOrDigit).ToArray());

                    int score = 0;

                    // Severe penalty for uninstallers, helpers, updaters
                    if (fileName.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("update", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("helper", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("crashpad", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("elevate", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase))
                    {
                        score -= 500;
                    }

                    // High match for exact or substring match with app name
                    if (string.Equals(fileName, appName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cleanFile, cleanAppName, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 200;
                    }
                    else if (cleanAppName.Length >= 3 && cleanFile.Length >= 3 &&
                             (cleanAppName.Contains(cleanFile, StringComparison.OrdinalIgnoreCase) ||
                              cleanFile.Contains(cleanAppName, StringComparison.OrdinalIgnoreCase)))
                    {
                        score += 100;
                    }

                    // Folder name match
                    if (string.Equals(fileName, folderName, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 80;
                    }

                    // Tie break with file size
                    long length = 0;
                    try { length = new FileInfo(exe).Length; } catch { }

                    return new { Path = exe, Score = score, Length = length };
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Length)
                .FirstOrDefault();

            return ranked?.Score > 0 ? ranked.Path : null;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? ExtractMsixLogo(string folder)
    {
        try
        {
            var assetsDir = Path.Combine(folder, "Assets");
            if (!Directory.Exists(assetsDir))
                assetsDir = folder;

            // Search for high-res square or logo PNGs
            var pngs = Directory.GetFiles(assetsDir, "*.png", SearchOption.AllDirectories)
                .Where(p =>
                {
                    var name = Path.GetFileName(p).ToLowerInvariant();
                    return name.Contains("logo") || name.Contains("square") || name.Contains("icon");
                })
                .OrderByDescending(p =>
                {
                    // Prefer larger sizes like 150x150, 256x256
                    try { return new FileInfo(p).Length; } catch { return 0; }
                })
                .ToList();

            if (pngs.Count > 0)
            {
                return IconExtractionService.LoadBitmap(pngs[0]);
            }
        }
        catch { }

        return null;
    }

    private static string? ParseExeFromString(string s)
    {
        var (exe, _) = UninstallService.SplitCommand(s);
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
    }

    private static void SavePng(ImageSource source, string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (source is not BitmapSource bs) return;

            var bytes = BitmapEncodingService.EncodePng(bs);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch { /* best effort */ }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    public static string SanitizeId(string id)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToUpperInvariant())));
    }

    private static string ReadOrigin(string path)
    {
        try { return new FileInfo(path + ".origin").Length <= 1024 ? File.ReadAllText(path + ".origin") : ""; }
        catch { return ""; }
    }

    private static void WriteOrigin(string path, string origin)
    {
        try { if (File.Exists(path)) File.WriteAllText(path + ".origin", origin); }
        catch { /* An optional icon cache must never interrupt an uninstall. */ }
    }
}
