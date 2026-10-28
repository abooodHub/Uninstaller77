using System.Diagnostics;

namespace WinUninstaller.Services;

/// <summary>The product page is the only external destination exposed by this action.</summary>
public static class ProductWebsite
{
    public const string Address = "https://abooodhub.github.io/Uninstaller77/";

    public static ProcessStartInfo CreateLaunchInfo() => new(Address)
    {
        UseShellExecute = true
    };
}
