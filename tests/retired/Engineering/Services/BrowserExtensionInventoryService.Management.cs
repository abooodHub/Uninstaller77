using System.Diagnostics;
using System.IO;
namespace WinUninstaller.Services;
public static partial class BrowserExtensionInventoryService
{
    /// <summary>Open the browser's built-in extensions page for the exact, still-present profile instance.</summary>
    public static void OpenExtensionManager(string browser, string profile, string extensionId, string installationLocation)
    {
        if (!IsSupportedBrowser(browser) || !IsSafeProfileName(profile) || extensionId.Length != 32 ||
            extensionId.Any(c => c is < 'a' or > 'p'))
            throw new InvalidDataException("The browser extension identity is invalid.");

        var current = ScanCurrentUser().Any(item =>
            item.Browser.Equals(browser, StringComparison.Ordinal) &&
            item.Profile.Equals(profile, StringComparison.Ordinal) &&
            item.Id.Equals(extensionId, StringComparison.Ordinal) &&
            item.InstallationLocation.Equals(installationLocation, StringComparison.OrdinalIgnoreCase));
        if (!current) throw new InvalidOperationException("This extension instance is no longer present. Refresh the inventory and try again.");

        var executable = FindBrowserExecutable(browser);
        if (executable is null) throw new InvalidOperationException($"{browser} was not found in its standard installation locations.");

        var page = browser switch
        {
            "Google Chrome" => "chrome://extensions",
            "Microsoft Edge" => "edge://extensions",
            "Brave" => "brave://extensions",
            _ => throw new InvalidDataException("This browser does not have a supported extensions page.")
        };
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        start.ArgumentList.Add($"--profile-directory={profile}");
        start.ArgumentList.Add(page);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not open {browser}.");
    }

    public static bool IsSupportedBrowser(string browser) => browser is "Google Chrome" or "Microsoft Edge" or "Brave";

    public static bool IsSafeProfileName(string profile) => profile == "Default" ||
        System.Text.RegularExpressions.Regex.IsMatch(profile, @"^Profile [1-9][0-9]{0,2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? FindBrowserExecutable(string browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = browser switch
        {
            "Google Chrome" => new[] { Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe") },
            "Microsoft Edge" => new[] { Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe") },
            "Brave" => new[] { Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"), Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"), Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe") },
            _ => []
        };
        return candidates.FirstOrDefault(File.Exists);
    }

}
