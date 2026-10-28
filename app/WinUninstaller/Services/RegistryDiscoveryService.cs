using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>
/// Discovers installed applications from Windows Registry (HKLM, HKCU, 64-bit, 32-bit)
/// and Windows Store / MSIX packages.
/// Identifies architecture, installer engine, and documented quiet uninstall flags.
/// </summary>
public class RegistryDiscoveryService
{
    private const string UninstallPath64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public sealed record DiscoveryDiagnostic(string Source, int Scanned, int Accepted, int Skipped,
        int Duplicates, int Errors, bool TimedOut, bool Cancelled, long ElapsedMs, string[] Messages,
        int SourceErrors = 0);
    public IReadOnlyList<DiscoveryDiagnostic> LastDiagnostics { get; private set; } = Array.Empty<DiscoveryDiagnostic>();

    private sealed class SourceScan(string source)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public int Scanned, Accepted, Skipped, Duplicates, Errors, SourceErrors;
        public bool TimedOut, Cancelled;
        public readonly List<string> Messages = new();
        public void Error(Exception ex) { Errors++; Messages.Add(ex.GetType().Name + ": " + ex.Message); }
        public void SourceError(Exception ex) { SourceErrors++; Messages.Add(ex.GetType().Name + ": " + ex.Message); }
        public DiscoveryDiagnostic Finish() => new(source, Scanned, Accepted, Skipped, Duplicates, Errors,
            TimedOut, Cancelled, _watch.ElapsedMilliseconds, Messages.Take(20).ToArray(), SourceErrors);
    }

    public List<InstalledApp> DiscoverApps(bool includeMsix = true, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<DiscoveryDiagnostic>();

        progress?.Report("Reading Registry");
        // 1. HKLM 64-bit
        diagnostics.Add(ScanHive(Registry.LocalMachine, UninstallPath64, "HKLM", "64-bit", apps, cancellationToken));

        // 2. HKLM 32-bit (WOW6432Node)
        if (Environment.Is64BitOperatingSystem)
        {
            diagnostics.Add(ScanHive(Registry.LocalMachine, UninstallPath32, "HKLM", "32-bit", apps, cancellationToken));
        }

        // 3. HKCU 64-bit
        diagnostics.Add(ScanHive(Registry.CurrentUser, UninstallPath64, "HKCU", "64-bit", apps, cancellationToken));

        // 4. HKCU 32-bit (rare, but supported)
        if (Environment.Is64BitOperatingSystem)
        {
            diagnostics.Add(ScanHive(Registry.CurrentUser, UninstallPath32, "HKCU", "32-bit", apps, cancellationToken));
        }

        // 5. MSIX / Windows Store Applications
        if (includeMsix)
        {
            progress?.Report("Checking Windows Apps");
            diagnostics.Add(DiscoverMsixApps(apps, cancellationToken));
        }

        progress?.Report("Preparing List");
        // Same-day installation is not bundle evidence. Classification stays unavailable.

        LastDiagnostics = diagnostics.ToArray();
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Logs");
            Directory.CreateDirectory(folder);
            var entry = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, processId = Environment.ProcessId,
                total = apps.Count, sources = diagnostics });
            lock (DiagnosticLogLock) File.AppendAllText(Path.Combine(folder, "discovery.jsonl"), entry + Environment.NewLine);
        }
        catch (Exception ex) { Debug.WriteLine("Discovery diagnostics could not be saved: " + ex); }

        return apps.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly object DiagnosticLogLock = new();
    internal static InstalledApp? ReadExactRegistration(string path)
    {
        if (!UninstallBroker.IsRegistrationPath(path)) throw new InvalidDataException("Unsupported uninstall registration identity.");
        var hiveName = path[..4].ToUpperInvariant();
        using var hive = RegistryKey.OpenBaseKey(hiveName == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
        using var key = hive.OpenSubKey(path[5..], writable: false);
        return key is null ? null : ParseSubKey(path[(path.LastIndexOf('\\') + 1)..], key, hiveName,
            path.Contains("\\WOW6432Node\\", StringComparison.OrdinalIgnoreCase) ? "32-bit" : "64-bit", path);
    }
    private static DiscoveryDiagnostic ScanHive(RegistryKey hive, string path, string hiveName, string arch,
        Dictionary<string, InstalledApp> apps, CancellationToken cancellationToken)
    {
        var scan = new SourceScan(hiveName + " " + arch);
        try
        {
            if (cancellationToken.IsCancellationRequested) { scan.Cancelled = true; return scan.Finish(); }
            using var key = hive.OpenSubKey(path, writable: false);
            if (key is null) { scan.Messages.Add("Uninstall root is absent."); return scan.Finish(); }
            foreach (var subName in key.GetSubKeyNames())
            {
                if (cancellationToken.IsCancellationRequested) { scan.Cancelled = true; break; }
                scan.Scanned++;
                try
                {
                    using var sub = key.OpenSubKey(subName, writable: false);
                    if (sub is null) { scan.Skipped++; continue; }
                    var app = ParseSubKey(subName, sub, hiveName, arch, $@"{hiveName}\{path}\{subName}");
                    if (app is null) { scan.Skipped++; continue; }
                    if (apps.TryAdd(app.Id, app)) scan.Accepted++;
                    else scan.Duplicates++;
                }
                catch (Exception ex) { scan.Error(ex); }
            }
        }
        catch (Exception ex) { scan.SourceError(ex); }
        return scan.Finish();
    }

    private static InstalledApp? ParseSubKey(string keyName, RegistryKey sub, string hiveName, string arch, string fullKeyPath)
    {
        // DisplayName is mandatory
        var name = GetStr(sub, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Skip system components unless explicitly an end-user tool
        if (GetInt(sub, "SystemComponent") == 1) return null;

        // Skip updates / hotfixes
        var releaseType = GetStr(sub, "ReleaseType");
        if (!string.IsNullOrEmpty(releaseType) &&
            (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
             releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
             releaseType.Contains("ServicePack", StringComparison.OrdinalIgnoreCase)))
            return null;

        var parentKey = GetStr(sub, "ParentKeyName");
        if (!string.IsNullOrEmpty(parentKey)) return null; // child update

        var uninstallString = GetStr(sub, "UninstallString");
        var quietUninstallString = GetStr(sub, "QuietUninstallString");

        // Skip if there's absolutely no uninstall string and not Windows Installer
        int isWinInstaller = (int)GetInt(sub, "WindowsInstaller");
        if (string.IsNullOrWhiteSpace(uninstallString) &&
            string.IsNullOrWhiteSpace(quietUninstallString) && isWinInstaller != 1)
            return null;

        // If MSI package has no uninstall string, generate standard MsiExec command
        if (string.IsNullOrWhiteSpace(uninstallString) && string.IsNullOrWhiteSpace(quietUninstallString) &&
            isWinInstaller == 1 && Guid.TryParseExact(keyName, "B", out _))
        {
            uninstallString = $"MsiExec.exe /X{keyName}";
        }

        var installerType = DetectInstallerType(string.IsNullOrWhiteSpace(uninstallString) ? quietUninstallString : uninstallString, sub);

        // Normalize MSI quiet string if not provided
        if (string.IsNullOrWhiteSpace(quietUninstallString))
        {
            if (installerType == InstallType.Msi)
            {
                var (_, msiArguments) = UninstallService.SplitCommand(Environment.ExpandEnvironmentVariables(uninstallString));
                var product = UninstallService.MsiProductOperand(msiArguments);
                if (product is not null)
                    quietUninstallString = $"MsiExec.exe /X{product} /qn /norestart";
            }
            else if (installerType == InstallType.InnoSetup)
            {
                var exe = ParseExeFromString(uninstallString);
                if (!string.IsNullOrEmpty(exe))
                    quietUninstallString = $"\"{exe}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            }
        }

        return new InstalledApp
        {
            Id = fullKeyPath,
            Name = name.Trim(),
            Publisher = GetStr(sub, "Publisher").Trim(),
            Version = GetStr(sub, "DisplayVersion").Trim(),
            DisplayIcon = GetStr(sub, "DisplayIcon").Trim(),
            InstallLocation = GetStr(sub, "InstallLocation").Trim(),
            UninstallString = uninstallString.Trim(),
            QuietUninstallString = quietUninstallString.Trim(),
            EstimatedSizeKB = GetInt(sub, "EstimatedSize"),
            InstallDate = GetStr(sub, "InstallDate").Trim(),
            IsSystemComponent = false,
            Source = "Registry",
            Architecture = arch,
            RegistryHive = hiveName,
            RegistryKeyPath = fullKeyPath,
            InstallerType = installerType
        };
    }

    private static InstallType DetectInstallerType(string uninstallString, RegistryKey sub)
    {
        // Many EXE installers use GUID-shaped keys (including Google Drive).
        // Never replace their registered EXE with msiexec merely for that reason.
        var (executable, _) = UninstallService.SplitCommand(Environment.ExpandEnvironmentVariables(uninstallString));
        if (UninstallService.IsWindowsInstallerExecutable(executable))
        {
            return InstallType.Msi;
        }

        if (uninstallString.Contains("unins000.exe", StringComparison.OrdinalIgnoreCase) ||
            GetStr(sub, "Inno Setup: App Path").Length > 0)
        {
            return InstallType.InnoSetup;
        }

        // A generic uninstall.exe/uninst.exe filename does not identify NSIS.
        // Keep the engine unspecified unless the registration gives stronger evidence.

        if (uninstallString.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) &&
            uninstallString.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return InstallType.Squirrel;
        }

        if (uninstallString.Contains("setup.exe", StringComparison.OrdinalIgnoreCase) &&
            uninstallString.Contains("-uninst", StringComparison.OrdinalIgnoreCase))
        {
            return InstallType.InstallShield;
        }

        return InstallType.StandardExe;
    }

    private static DiscoveryDiagnostic DiscoverMsixApps(Dictionary<string, InstalledApp> apps, CancellationToken cancellationToken)
    {
        var scan = new SourceScan("MSIX/AppX");
        try
        {
            if (cancellationToken.IsCancellationRequested) { scan.Cancelled = true; return scan.Finish(); }
            // Use lightweight PowerShell script to query non-framework end-user apps
            var psi = CreateMsixQueryStartInfo();

            using var proc = Process.Start(psi);
            if (proc == null) { scan.SourceError(new InvalidOperationException("PowerShell did not start.")); return scan.Finish(); }

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var wait = Stopwatch.StartNew();
            while (!proc.WaitForExit(100))
            {
                if (!cancellationToken.IsCancellationRequested && wait.ElapsedMilliseconds < 7000) continue;
                scan.Cancelled = cancellationToken.IsCancellationRequested;
                scan.TimedOut = !scan.Cancelled;
                scan.Messages.Add(scan.Cancelled ? "Package query cancelled." : "Package query exceeded 7000 ms.");
                proc.Kill(entireProcessTree: true); // Only this query's child process.
                proc.WaitForExit(2000);
                return scan.Finish();
            }
            string json = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
            {
                scan.SourceErrors++;
                scan.Messages.Add($"PowerShell exit={proc.ExitCode}; stderr={error[..Math.Min(2000, error.Length)]}");
            }
            if (string.IsNullOrWhiteSpace(json)) return scan.Finish();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            void ProcessElement(JsonElement el)
            {
                scan.Scanned++;
                if (!el.TryGetProperty("Name", out var nameProp)) { scan.Skipped++; return; }
                var rawName = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(rawName)) { scan.Skipped++; return; }

                // Skip internal framework packages
                if (rawName.StartsWith("Microsoft.NET.", StringComparison.OrdinalIgnoreCase) ||
                    rawName.StartsWith("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase) ||
                    rawName.StartsWith("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase))
                    { scan.Skipped++; return; }

                var publisher = el.TryGetProperty("Publisher", out var pubProp) ? CleanPublisher(pubProp.GetString()) : "";
                var version = el.TryGetProperty("Version", out var verProp) ? verProp.GetString() ?? "" : "";
                var loc = el.TryGetProperty("InstallLocation", out var locProp) ? locProp.GetString() ?? "" : "";
                var fullName = el.TryGetProperty("PackageFullName", out var fnProp) ? fnProp.GetString() ?? "" : "";
                var famName = el.TryGetProperty("PackageFamilyName", out var famProp) ? famProp.GetString() ?? "" : "";

                var cleanName = FormatPackageDisplayName(rawName);
                if (cleanName.All(char.IsDigit)) { scan.Skipped++; return; }

                // Publisher identity alone does not mean a Store app is built into Windows.
                bool isBuiltIn = IsBuiltInPackageName(rawName);

                var app = new InstalledApp
                {
                    Id = fullName,
                    Name = cleanName,
                    Publisher = publisher,
                    Version = version,
                    InstallLocation = loc,
                    UninstallString = $"powershell.exe -NoProfile -Command \"Remove-AppxPackage -Package '{fullName}'\"",
                    QuietUninstallString = $"powershell.exe -NoProfile -Command \"Remove-AppxPackage -Package '{fullName}'\"",
                    Source = "MSIX",
                    PackageFamilyName = famName,
                    Architecture = fullName.Contains("x64") ? "64-bit" : (fullName.Contains("x86") ? "32-bit" : "Neutral"),
                    RegistryHive = "AppX",
                    RegistryKeyPath = fullName,
                    InstallerType = InstallType.Msix,
                    EstimatedSizeKB = EstimateDirectorySizeKB(loc),
                    IsBuiltInWindowsApp = isBuiltIn,
                    IsThirdPartyWindowsApp = !isBuiltIn
                };

                if (apps.TryAdd(app.Id, app)) scan.Accepted++;
                else scan.Duplicates++;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    ProcessElement(el);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                ProcessElement(root);
            }
        }
        catch (Exception ex) { scan.SourceError(ex); }
        return scan.Finish();
    }

    internal static ProcessStartInfo CreateMsixQueryStartInfo() => new()
    {
        FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
        Arguments = "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; Get-AppxPackage -ErrorAction Stop | Where-Object { -not $_.IsFramework -and $_.NonRemovable -ne $true } | Select-Object Name, Publisher, Version, InstallLocation, PackageFullName, PackageFamilyName | ConvertTo-Json -Compress\"",
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8
    };

    public static bool IsBuiltInPackageName(string packageName)
    {
        // A publisher prefix is not enough: Microsoft Store products such as
        // Visual Studio Code are not built into Windows. Keep the system
        // namespace rule and explicitly recognize well-known inbox app IDs.
        if (packageName.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
            packageName.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase))
            return true;

        return packageName.StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.StorePurchaseApp", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.Paint", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.ScreenSketch", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.HEIFImageExtension", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.AV1VideoExtension", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.AVCEncoderVideoExtension", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.VP9VideoExtensions", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanPublisher(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var match = Regex.Match(raw, @"(?:CN|O)=([^,]+)", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim('"', ' ');
        return raw.Trim();
    }

    private static string FormatPackageDisplayName(string rawName)
    {
        // Split camel case or dots, e.g. "RealtekSemiconductorCorp.RealtekAudioControl" -> "Realtek Audio Control"
        var lastDot = rawName.LastIndexOf('.');
        var shortName = lastDot > 0 && lastDot < rawName.Length - 1 ? rawName[(lastDot + 1)..] : rawName;
        return Regex.Replace(shortName, "([a-z])([A-Z])", "$1 $2");
    }

    private static long EstimateDirectorySizeKB(string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                try { bytes += new FileInfo(f).Length; } catch { }
            }
            return bytes / 1024;
        }
        catch { return 0; }
    }

    private static string? ParseExeFromString(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 0) return s[1..end];
        }
        var space = s.IndexOf(' ');
        var exe = space > 0 ? s[..space] : s;
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
    }

    private static string GetStr(RegistryKey key, string name)
    {
        try { return key.GetValue(name) as string ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static long GetInt(RegistryKey key, string name)
    {
        try
        {
            var val = key.GetValue(name);
            return val is int i ? i : (val is long l ? l : 0L);
        }
        catch { return 0L; }
    }
}
