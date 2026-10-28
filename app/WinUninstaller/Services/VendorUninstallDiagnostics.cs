using System.Globalization;
using System.IO;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Optional vendor evidence for this exact launch. Never establishes removal or starts an action.</summary>
internal static class VendorUninstallDiagnostics
{
    internal const string RiotDependencyMessage = "Riot Client cannot be removed while Riot games remain installed.";
    internal const string RiotDependencyAdvice = "The Riot uninstaller refused removal because Riot games are still installed. Remove the games you choose separately, then retry Riot Client. Only applications explicitly confirmed in this queue can run; no Riot games are added automatically.";

    internal static bool HasRiotDependencyFailure(InstalledApp app, string executable, int processId,
        DateTimeOffset launchedAt, string? logDirectory = null)
    {
        if (processId <= 0 || !app.Name.Equals("Riot Client", StringComparison.OrdinalIgnoreCase) ||
            !app.Publisher.Contains("Riot", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(executable).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var root = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Riot Games", "Riot Client", "Logs", "Launcher");
            for (var path = new DirectoryInfo(root); path is not null; path = path.Parent)
                if ((path.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var suffix = "_" + processId.ToString(CultureInfo.InvariantCulture) + "_Launcher.log";
            foreach (var file in Directory.EnumerateFiles(root, "*" + suffix).Take(8))
            {
                var info = new FileInfo(file);
                var stamp = Path.GetFileName(file)[..19];
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is <= 0 or > 1_048_576 ||
                    !DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var localStamp) ||
                    Math.Abs((new DateTimeOffset(localStamp) - launchedAt).TotalSeconds) > 120 ||
                    info.LastWriteTimeUtc < launchedAt.UtcDateTime.AddSeconds(-5)) continue;
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(input);
                var buffer = new char[1_048_577];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                if (count == buffer.Length) continue;
                var content = new string(buffer, 0, count);
                if (content.Contains("Cannot uninstall Riot Client due to Riot Games are still installed", StringComparison.Ordinal) ||
                    content.Contains("Cannot uninstall because the riot games are still installed, please remove all games prior to uninstalling riot client", StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return false;
    }
}
