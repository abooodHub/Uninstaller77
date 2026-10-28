using CommunityToolkit.Mvvm.ComponentModel;

namespace WinUninstaller.Models;

public enum ResidualType
{
    Directory,
    File,
    RegistryKey,
    RegistryValue
}

public enum ConfidenceLevel
{
    High,
    Medium,
    Low
}

public partial class ResidualItem : ObservableObject
{
    public ResidualType Type { get; set; }
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public ConfidenceLevel Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool CanClean { get; set; }
    public string Title => string.IsNullOrEmpty(Path) ? string.Empty : (System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path);

    [ObservableProperty]
    private bool _isSelected = true;

    public string SizeDisplay => SizeBytes switch
    {
        0 => Type is ResidualType.RegistryKey or ResidualType.RegistryValue ? "Registry" : "Unknown",
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{SizeBytes / 1024.0 / 1024.0 / 1024.0:F1} GB"
    };

    public string TypeDisplay => Type switch
    {
        ResidualType.Directory => "📁 Folder",
        ResidualType.File => "📄 File",
        ResidualType.RegistryKey => "🔑 Registry Key",
        ResidualType.RegistryValue => "🏷 Registry Value",
        _ => "Item"
    };

    public string ConfidenceDisplay => Confidence switch
    {
        ConfidenceLevel.High => "High",
        ConfidenceLevel.Medium => "Medium",
        ConfidenceLevel.Low => "Low",
        _ => "Unknown"
    };
}
