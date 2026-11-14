using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace WinUninstaller.Services;

internal static partial class BrowserExtensionManager
{
    internal static string? FindExecutable(string browser)
    {
        var folders = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
            .Select(folder => Environment.GetFolderPath(folder)).Where(path => path.Length != 0);
        var relativePaths = browser switch
        {
            "Google Chrome" => new[] { @"Google\Chrome\Application\chrome.exe" },
            "Microsoft Edge" => new[] { @"Microsoft\Edge\Application\msedge.exe" },
            "Brave" => new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" },
            "Vivaldi" => new[] { @"Vivaldi\Application\vivaldi.exe" },
            "Opera" => new[] { @"Opera\launcher.exe", @"Programs\Opera\launcher.exe" },
            _ => [],
        };
        return folders.SelectMany(root => relativePaths.Select(path => Path.Combine(root, path))).FirstOrDefault(File.Exists);
    }

    internal static bool IsSupportedProfile(string browser, string profile) => browser == "Opera"
        ? profile == "Opera Stable" : profile == "Default" || ProfileName().IsMatch(profile);

    internal static bool IsExtensionId(string id) => id.Length == 32 && id.All(c => c is >= 'a' and <= 'p');

    internal static ProcessStartInfo CreateLaunch(string browser, string profile, string? id)
    {
        if (!IsSupportedProfile(browser, profile) || (id is not null && !IsExtensionId(id)))
            throw new InvalidDataException("Invalid browser extension identity.");
        var page = browser switch
        {
            "Google Chrome" => "chrome://extensions/",
            "Microsoft Edge" => "edge://extensions/",
            "Brave" => "brave://extensions/",
            "Vivaldi" => "vivaldi://extensions/",
            "Opera" => "opera://extensions/",
            _ => throw new InvalidDataException("Unsupported browser."),
        };
        var executable = FindExecutable(browser) ?? throw new FileNotFoundException("Browser executable unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (browser != "Opera") start.ArgumentList.Add("--profile-directory=" + profile);
        start.ArgumentList.Add(id is not null && (browser is "Google Chrome" or "Microsoft Edge" or "Brave") ? page + "?id=" + id : page);
        return start;
    }

    [GeneratedRegex(@"\AProfile [1-9][0-9]{0,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileName();
}
