using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinUninstaller.Models;

public partial class InstalledApp : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayIcon { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public long EstimatedSizeKB { get; set; }
    public string InstallDate { get; set; } = string.Empty;
    public bool IsSystemComponent { get; set; }
    public string Source { get; set; } = "Registry"; // Registry | MSIX | Logged
    public string PackageFamilyName { get; set; } = string.Empty;
    public string CachedIconPath { get; set; } = string.Empty;

    public string Architecture { get; set; } = "64-bit"; // 64-bit | 32-bit
    public string RegistryHive { get; set; } = "HKLM"; // HKLM | HKCU
    public string RegistryKeyPath { get; set; } = string.Empty;
    public InstallType InstallerType { get; set; } = InstallType.StandardExe;

    // Subcategory flags
    public bool IsBundleware { get; set; }
    public bool IsLogged { get; set; }
    public bool IsBuiltInWindowsApp { get; set; }
    public bool IsThirdPartyWindowsApp { get; set; }
    public string Description { get; set; } = string.Empty;

    // UI-only icon image
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private ImageSource? _iconImage;

    [ObservableProperty]
    private bool _isBatchSelected;

    public DateTime? ParsedInstallDate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InstallDate)) return null;
            var clean = InstallDate.Trim();
            if (clean.Length == 8 && DateTime.TryParseExact(clean, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt1))
                return dt1;
            if (clean.Length is 10 or 13 && long.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
            {
                try { return (clean.Length == 10 ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : DateTimeOffset.FromUnixTimeMilliseconds(timestamp)).LocalDateTime; }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            if (DateTime.TryParse(clean, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt2))
                return dt2;
            if (DateTime.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt3))
                return dt3;
            return null;
        }
    }

    public string DisplayInstallDate => ParsedInstallDate?.ToString("yyyy-MM-dd") ?? (!string.IsNullOrEmpty(InstallDate) ? InstallDate : "Unknown");

    public bool IsLargeApp => EstimatedSizeKB >= 500 * 1024; // >= 500 MB

    public bool IsRecentApp
    {
        get
        {
            var date = ParsedInstallDate;
            if (!date.HasValue) return false;
            var age = (DateTime.Now - date.Value).TotalDays;
            return age is >= 0 and <= 90;
        }
    }

    public string SizeDisplay => EstimatedSizeKB switch
    {
        0 => "Unknown",
        < 1024 => $"{EstimatedSizeKB} KB",
        < 1024 * 1024 => $"{EstimatedSizeKB / 1024.0:F1} MB",
        _ => $"{EstimatedSizeKB / 1024.0 / 1024.0:F1} GB"
    };

    public string TypeDisplay => InstallerType switch
    {
        InstallType.Msi => "MSI Package",
        InstallType.InnoSetup => "Inno Setup",
        InstallType.Nsis => "NSIS",
        InstallType.Squirrel => "Squirrel",
        InstallType.Msix => "MSIX / Store",
        InstallType.InstallShield => "InstallShield",
        _ => "Standard EXE"
    };

    public bool HasQuietUninstall => !string.IsNullOrWhiteSpace(QuietUninstallString) ||
                                     InstallerType is InstallType.Msi or InstallType.InnoSetup or InstallType.Msix;

    public string SafeUninstallCommand => PreviewCommand(UninstallString);
    public string SafeQuietUninstallCommand => PreviewCommand(QuietUninstallString);

    private static string PreviewCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "Not provided";
        var trimmed = command.Trim();
        var exeEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0) return "Command details hidden for privacy";
        var executable = trimmed[..(exeEnd + 4)].Trim().Trim('"');
        return executable + "  [arguments hidden for privacy]";
    }
}
