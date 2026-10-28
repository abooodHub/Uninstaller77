using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

internal static class Program
{
    public const string RegistryParent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static int Main(string[] args)
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (!(bios?.GetValue("SystemManufacturer")?.ToString() ?? "").Contains("VMware", StringComparison.OrdinalIgnoreCase)) return 90;
        if (args.Length != 3 || !Guid.TryParseExact(args[1], "N", out var id) || args[2] is not ("success" or "child" or "failure" or "delayed")) return 87;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Uninstaller77Phase10", "Fixtures", id.ToString("N"));
        var keyName = "Uninstaller77_Phase10_" + id.ToString("N");
        var key = RegistryParent + "\\" + keyName;
        var command = args[0];
        try
        {
            if (command == "--install")
            {
                if (Directory.Exists(root) || Registry.CurrentUser.OpenSubKey(key) is not null) return 183;
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "owner.txt"), id.ToString("N"));
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Phase10Fixture.*"))
                    File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
                File.WriteAllText(Path.Combine(root, "application.txt"), "Disposable application " + id);
                File.WriteAllText(Path.Combine(root, "leftover.txt"), "Deliberate fixture residual " + id);
                using var entry = Registry.CurrentUser.CreateSubKey(key);
                entry.SetValue("DisplayName", "U77 Phase10 Fixture — " + args[2] + " — " + id.ToString("N")[..8]);
                entry.SetValue("Publisher", "Uninstaller 77 Fixture Studio");
                entry.SetValue("DisplayVersion", "1.0.10");
                entry.SetValue("InstallLocation", root);
                entry.SetValue("DisplayIcon", Path.Combine(root, "Phase10Fixture.exe"));
                entry.SetValue("UninstallString", $"\"{Path.Combine(root, "Phase10Fixture.exe")}\" --remove {id:N} {args[2]}");
                entry.SetValue("EstimatedSize", 256);
                entry.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                Log("installed", 0); return 0;
            }
            if (!Directory.Exists(root) || File.ReadAllText(Path.Combine(root, "owner.txt")) != id.ToString("N")) return 91;
            if (command == "--remove" && args[2] == "child")
            {
                Log("launcher-start", 0);
                Process.Start(new ProcessStartInfo(Path.Combine(root, "Phase10Fixture.exe"), $"--worker {id:N} child") { UseShellExecute = false });
                Log("launcher-exit", 0); return 0;
            }
            if (command is not ("--remove" or "--worker")) return 87;
            Log("worker-start", 0);
            if (args[2] == "failure") { Log("worker-exit", 1603); return 1603; }
            Thread.Sleep(args[2] == "delayed" ? 3500 : args[2] == "child" ? 2000 : 900);
            File.Delete(Path.Combine(root, "application.txt"));
            Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
            Log("worker-exit", 0); return 0;
        }
        catch (Exception ex) { if (Directory.Exists(root)) Log("error: " + ex.Message, 1); return 1; }

        void Log(string stage, int code) => File.AppendAllText(Path.Combine(root, "execution.jsonl"), JsonSerializer.Serialize(new
        { timestamp = DateTimeOffset.UtcNow, stage, code, pid = Environment.ProcessId, id = id.ToString("N"), behavior = args[2] }) + Environment.NewLine);
    }
}
