using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Phase6Fixture;

internal static class Program
{
    private const string Name = "WinUninstaller Test Application";
    private const string Publisher = "WinUninstaller Test Studio";
    private const string KeyParent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string FixtureParent = "WinUninstallerPhase6";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            MessageBox.Show("This isolated application is used to validate WinUninstaller.", Name);
            return 0;
        }
        if (args.Length != 3 || !Guid.TryParseExact(args[1], "N", out var id) ||
            args[2] is not ("success" or "failure")) return 87;

        var mode = args[0];
        if (mode is not ("--install" or "--uninstall" or "--cleanup")) return 87;
        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FixtureParent, id.ToString("N")));
        var parent = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FixtureParent));
        if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 87;
        var install = Path.Combine(root, "App");
        var keyName = "WinUninstaller_Phase6_" + id.ToString("N");
        var keyPath = KeyParent + "\\" + keyName;
        var logPath = Path.Combine(root, "execution.jsonl");

        try
        {
            if (mode == "--install")
            {
                if (Directory.Exists(root) || Registry.CurrentUser.OpenSubKey(keyPath) != null) return 183;
                Directory.CreateDirectory(install);
                CopyExecutable("WinUninstaller Test Application");
                CopyExecutable("uninstall");
                File.WriteAllText(Path.Combine(install, "leftover.txt"), "Phase 6 owned residual " + id.ToString("N"));
                using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
                key.SetValue("DisplayName", Name);
                key.SetValue("Publisher", Publisher);
                key.SetValue("DisplayVersion", "1.0.6");
                key.SetValue("InstallLocation", install);
                key.SetValue("DisplayIcon", Path.Combine(install, "WinUninstaller Test Application.exe"));
                key.SetValue("UninstallString", $"\"{Path.Combine(install, "uninstall.exe")}\" --uninstall {id:N} {args[2]}");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("EstimatedSize", 64, RegistryValueKind.DWord);
                Log(logPath, mode, id, args[2], 0, keyPath, install);
                return 0;
            }

            if (Registry.CurrentUser.OpenSubKey(keyPath) == null) return 2;
            if (!Directory.Exists(install)) return 3;

            if (mode == "--uninstall" && args[2] == "failure")
            {
                Log(logPath, mode, id, args[2], 1603, keyPath, install);
                return 1603;
            }

            if (mode == "--uninstall")
            {
                var appExe = Path.Combine(install, "WinUninstaller Test Application.exe");
                if (File.Exists(appExe)) File.Delete(appExe);
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
                Log(logPath, mode, id, args[2], 0, keyPath, install);
                return 0;
            }

            // Test-only cleanup removes only the exact GUID registration and install folder.
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Directory.Delete(install, recursive: true);
            Log(logPath, mode, id, args[2], 0, keyPath, install);
            return 0;
        }
        catch (Exception ex)
        {
            try { Log(logPath, mode + "-error", id, args[2], 1, keyPath, install, ex.Message); } catch { }
            return 1;
        }

        void CopyExecutable(string targetName)
        {
            var source = Path.GetDirectoryName(Environment.ProcessPath!)!;
            File.Copy(Environment.ProcessPath!, Path.Combine(install, targetName + ".exe"));
            foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var supportName = "Phase6Fixture" + extension;
                var destination = Path.Combine(install, supportName);
                if (!File.Exists(destination)) File.Copy(Path.Combine(source, supportName), destination);
            }
        }
    }

    private static void Log(string path, string operation, Guid id, string behavior, int exit,
        string key, string install, string? error = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var row = new
        {
            utc = DateTimeOffset.UtcNow,
            operation,
            fixtureId = id.ToString("N"),
            behavior,
            pid = Environment.ProcessId,
            executable = Environment.ProcessPath,
            commandLine = Environment.CommandLine,
            registryKey = "HKCU\\" + key,
            installLocation = install,
            exitCode = exit,
            error
        };
        File.AppendAllText(path, JsonSerializer.Serialize(row) + Environment.NewLine);
    }
}
