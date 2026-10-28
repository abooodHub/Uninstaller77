using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Registers a per-user Explorer verb for .lnk files; it never runs an uninstaller itself.</summary>
public static class ShellUninstallContextMenuService
{
    private const string VerbKeyPath = @"Software\Classes\lnkfile\shell\Uninstaller77";

    public static void Register(string languageCode)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;

            using var verb = Registry.CurrentUser.CreateSubKey(VerbKeyPath, writable: true);
            if (verb is null) return;

            verb.SetValue("MUIVerb", languageCode == "ar" ? "إلغاء التثبيت عبر Uninstaller 77" : "Uninstall with Uninstaller 77", RegistryValueKind.String);
            verb.SetValue("Icon", $"\"{executable}\",0", RegistryValueKind.String);
            verb.SetValue("Position", "Top", RegistryValueKind.String);
            using var command = verb.CreateSubKey("command", writable: true);
            command?.SetValue(string.Empty, $"\"{executable}\" --shell-uninstall \"%1\"", RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Explorer uninstall verb registration unavailable: {ex.GetType().Name}");
        }
    }

    public static string? ResolveShortcutTarget(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || shortcutPath.Length > 32768 ||
            !Path.GetExtension(shortcutPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(shortcutPath)) return null;

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true);
            shell = Activator.CreateInstance(shellType!);
            if (shell is null) return null;
            dynamic automation = shell;
            shortcut = automation.CreateShortcut(Path.GetFullPath(shortcutPath));
            dynamic link = shortcut;
            string target = link.TargetPath;
            if (string.IsNullOrWhiteSpace(target)) return null;
            target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
            return Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException or TypeLoadException)
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    public static IReadOnlyList<InstalledApp> FindMatchingApps(string targetPath, IEnumerable<InstalledApp> apps)
    {
        var target = NormalizePath(targetPath);
        if (target is null) return Array.Empty<InstalledApp>();

        var matches = new List<(InstalledApp App, int Score)>();
        foreach (var app in apps.Where(item => item.Source == "Registry" && !item.IsSystemComponent))
        {
            var score = 0;
            var displayIcon = GetDisplayIconExecutable(app.DisplayIcon);
            if (displayIcon is not null && PathEquals(target, displayIcon)) score = 100000;

            var installRoot = NormalizePath(app.InstallLocation);
            if (installRoot is not null && IsWithinDirectory(target, installRoot))
                score = Math.Max(score, Math.Min(installRoot.Length, 99999));

            if (score > 0) matches.Add((app, score));
        }

        if (matches.Count == 0) return Array.Empty<InstalledApp>();
        var highest = matches.Max(match => match.Score);
        return matches.Where(match => match.Score == highest).Select(match => match.App).ToArray();
    }

    internal static string? GetDisplayIconExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith('"'))
        {
            var closingQuote = text.IndexOf('"', 1);
            if (closingQuote > 1) text = text[1..closingQuote];
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out _)) text = text[..comma].Trim();
            var executableEnd = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0) text = text[..(executableEnd + 4)];
        }
        return NormalizePath(text);
    }

    private static string? NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    private static bool PathEquals(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinDirectory(string target, string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
