namespace WinUninstaller.Models;

public class UninstallRecord
{
    public int Id { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string CachedIconPath { get; set; } = string.Empty;
    public string UninstallDate { get; set; } = string.Empty;
    public string InstallerType { get; set; } = "StandardExe";
    public int ExitCode { get; set; }
    // Old numeric sentinels remain stored for compatibility; display only an
    // exact exit-evidence line, never infer a launched process from Outcome.
    public int? ObservedExitCode => Notes.Split('\n').Any(line => line.TrimEnd('\r') ==
        "Exit code: " + ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)) ? ExitCode : null;
    public bool Success { get; set; }
    public string Outcome { get; set; } = "";
    public string ResidualStatus { get; set; } = "Not Scanned";
    public int CleanedItemsCount { get; set; }
    public long CleanedSizeBytes { get; set; }
    public string Notes { get; set; } = string.Empty;

    public string CleanedSizeDisplay => CleanedSizeBytes switch
    {
        0 => "0 B",
        < 1024 => $"{CleanedSizeBytes} B",
        < 1024 * 1024 => $"{CleanedSizeBytes / 1024.0:F1} KB",
        _ => $"{CleanedSizeBytes / 1024.0 / 1024.0:F1} MB"
    };
}
