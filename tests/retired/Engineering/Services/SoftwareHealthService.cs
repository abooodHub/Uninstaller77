using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>
/// Evidence-based software health inspection across 9 distinct categories,
/// mapping 1:1 to the reference Software Health module.
/// All checks are read-only during scan; cleanup requires explicit user intent.
/// </summary>
public static class SoftwareHealthService
{
    public sealed record HealthItem(
        string Id,
        string Title,
        string Category,
        string Description,
        string TargetPath,
        long SizeBytes,
        bool cleanable,
        string Evidence
    )
    {
        public bool Cleanable => cleanable;
    }

    public sealed record CategoryResult(
        string Key,
        string TitleEn,
        string TitleAr,
        string DescriptionEn,
        string DescriptionAr,
        string IconName,
        List<HealthItem> Items,
        bool IsSupported = true
    )
    {
        public int Count => Items.Count;
        public long TotalSizeBytes => Items.Sum(i => i.SizeBytes);
    }

    public sealed record HealthReport(
        DateTime Timestamp,
        int TotalIssues,
        long ReclaimableBytes,
        List<CategoryResult> Categories
    );

    public sealed record Finding(string Kind, string AppId, string AppName, string Details);

    public static IReadOnlyList<Finding> Scan(IEnumerable<InstalledApp> apps)
    {
        var findings = new List<Finding>();
        foreach (var app in apps)
        {
            if (string.Equals(app.Source, "MSIX", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(app.UninstallString))
            {
                findings.Add(new Finding("NoUninstallCommand", app.Id, app.Name, "Application has no uninstall command registered."));
                continue;
            }

            var unquoted = app.UninstallString.Trim();
            if (unquoted.StartsWith("\""))
            {
                var end = unquoted.IndexOf('\"', 1);
                if (end > 1)
                    unquoted = unquoted.Substring(1, end - 1);
            }
            else
            {
                var space = unquoted.IndexOf(' ');
                if (space > 0)
                    unquoted = unquoted.Substring(0, space);
            }

            if (Path.IsPathRooted(unquoted) && !File.Exists(unquoted))
            {
                findings.Add(new Finding("MissingUninstaller", app.Id, app.Name, $"Uninstaller executable was not found: {unquoted}"));
            }
        }
        return findings;
    }

    public static HealthReport ScanAll(IEnumerable<InstalledApp> apps)
    {
        var appList = apps.ToList();
        var categories = new List<CategoryResult>
        {
            UnavailableCategory("leftovers", "Uninstallation Leftovers", "بقايا إزالة التثبيت", "No reliable orphan proof is available.", "لا يتوفر دليل موثوق يثبت أن الملفات متروكة.", "FileText"),
            UnavailableCategory("outdated", "Outdated Software", "برامج قديمة", "No trusted version feed is configured.", "لا توجد قاعدة موثوقة لمقارنة الإصدارات.", "CloudDownload"),
            UnavailableCategory("redundant", "Software Redundant Files", "الملفات الزائدة", "File age alone cannot prove a file is safe to remove.", "عمر الملف وحده لا يثبت أمان حذفه.", "Rocket"),
            UnavailableCategory("hibernation", "Software Hibernation", "إسبات البرامج", "No safe app sleep integration is available.", "لا يتوفر تكامل آمن لإسبات التطبيقات.", "Moon"),
            UnavailableCategory("permissions", "Software Permissions", "أذونات البرامج", "Permission grants are not yet mapped to verified app identities.", "لم تُربط الأذونات بعد بهويات تطبيقات موثوقة.", "Lock"),
            UnavailableCategory("notifications", "Disturbing Notifications", "الإخطارات المزعجة", "Notification settings are not yet mapped to verified app identities.", "لم تُربط إعدادات الإشعارات بعد بهويات تطبيقات موثوقة.", "BellOff"),
            ScanUninstallIssues(appList),
            UnavailableCategory("packages", "Installation Files", "ملفات التثبيت", "No evidence links downloaded installers to an active installation.", "لا يوجد دليل يربط ملفات التثبيت المنزّلة ببرنامج مثبت.", "Package"),
            UnavailableCategory("malicious", "Malware / Adware", "البرامج الضارة", "No reputation or malware intelligence source is configured.", "لا توجد خدمة سمعة أو معلومات موثوقة عن البرمجيات الضارة.", "ShieldAlert")
        };

        int totalIssues = categories.Where(c => c.IsSupported).Sum(c => c.Count);
        long reclaimable = categories.Where(c => c.IsSupported).Sum(c => c.TotalSizeBytes);

        return new HealthReport(DateTime.Now, totalIssues, reclaimable, categories);
    }

    private static CategoryResult UnavailableCategory(string key, string titleEn, string titleAr, string descEn, string descAr, string icon) =>
        new(key, titleEn, titleAr, descEn, descAr, icon, new List<HealthItem>(), IsSupported: false);

    // 1. Uninstallation Leftovers (بقايا إزالة التثبيت)
    private static CategoryResult ScanLeftovers(List<InstalledApp> apps)
    {
        var items = new List<HealthItem>();
        var registeredNames = new HashSet<string>(apps.Select(a => a.Name.ToLowerInvariant()));

        // Check for broken registry uninstall entries where the install path no longer exists
        foreach (var app in apps.Where(a => a.Source == "Registry" && !a.IsSystemComponent))
        {
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) &&
                Path.IsPathFullyQualified(app.InstallLocation) &&
                !Directory.Exists(app.InstallLocation))
            {
                items.Add(new HealthItem(
                    $"leftover-reg-{app.Id}",
                    app.Name,
                    "leftovers",
                    "Registry uninstall entry remains, but install directory no longer exists on disk.",
                    app.RegistryKeyPath,
                    0,
                    cleanable: false,
                    $"Key: {app.RegistryKeyPath} | Missing Dir: {app.InstallLocation}"
                ));
            }
        }

        // Check for orphaned AppData folders from uninstalled applications
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Directory.Exists(localAppData))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(localAppData).Take(100))
                {
                    var folderName = Path.GetFileName(dir);
                    if (folderName.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals("WinUninstaller", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // If folder doesn't match any installed app and is older than 60 days
                    var dirInfo = new DirectoryInfo(dir);
                    if ((DateTime.Now - dirInfo.LastWriteTime).TotalDays > 60 &&
                        !registeredNames.Any(n => n.Contains(folderName.ToLowerInvariant())))
                    {
                        long size = SafeGetDirSize(dir);
                        if (size > 500_000) // > 500 KB
                        {
                            items.Add(new HealthItem(
                                $"leftover-dir-{folderName}",
                                folderName,
                                "leftovers",
                                "Orphaned AppData folder with no active application registration.",
                                dir,
                                size,
                                cleanable: false,
                                $"Folder: {dir} | Last modified: {dirInfo.LastWriteTime:yyyy-MM-dd}"
                            ));
                        }
                    }
                }
            }
            catch { }
        }

        return new CategoryResult(
            "leftovers",
            "Uninstallation Leftovers",
            "بقايا إزالة التثبيت",
            "Delete uninstallation leftovers to free up more disk space",
            "حذف بقايا إزالة التثبيت لتحرير مساحة أكبر على القرص",
            "FileText",
            items
        );
    }

    // 2. Outdated Software (برامج قديمة)
    private static CategoryResult ScanOutdated(List<InstalledApp> apps)
    {
        var items = new List<HealthItem>();
        foreach (var app in apps.Where(a => a.Source == "Registry" && !a.IsSystemComponent))
        {
            var date = app.ParsedInstallDate;
            if (date.HasValue && (DateTime.Now - date.Value).TotalDays > 730) // Installed > 2 years ago
            {
                items.Add(new HealthItem(
                    $"outdated-{app.Id}",
                    app.Name,
                    "outdated",
                    $"Software has not been updated in over 2 years (Installed: {date.Value:yyyy-MM-dd}).",
                    app.InstallLocation,
                    0,
                    cleanable: false,
                    $"Version: {app.Version} | Install Date: {date.Value:yyyy-MM-dd}"
                ));
            }
        }

        return new CategoryResult(
            "outdated",
            "Outdated Software",
            "برامج قديمة",
            "Update outdated software to reduce security risks",
            "تحديث البرامج القديمة للحد من المخاطر الأمنية",
            "CloudDownload",
            items
        );
    }

    // 3. Software Redundant Files (ملفات البرامج الزائدة عن الحاجة)
    private static CategoryResult ScanRedundantFiles()
    {
        var items = new List<HealthItem>();

        // 1. Crash Dumps
        var crashDumps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
        if (Directory.Exists(crashDumps))
        {
            try
            {
                var files = Directory.GetFiles(crashDumps, "*.dmp");
                long size = files.Sum(f => new FileInfo(f).Length);
                if (files.Length > 0)
                {
                    items.Add(new HealthItem(
                        "redundant-crashdumps",
                        "Application Crash Dumps",
                        "redundant",
                        $"{files.Length} crash dump files accumulated from previous application crashes.",
                        crashDumps,
                        size,
                        cleanable: false,
                        $"{files.Length} .dmp files in {crashDumps}"
                    ));
                }
            }
            catch { }
        }

        // 2. User Temp Cache
        var tempDir = Path.GetTempPath();
        if (Directory.Exists(tempDir))
        {
            try
            {
                var oldTempFiles = Directory.GetFiles(tempDir)
                    .Select(f => new FileInfo(f))
                    .Where(fi => (DateTime.Now - fi.LastWriteTime).TotalDays > 7)
                    .ToList();

                long size = oldTempFiles.Sum(f => f.Length);
                if (oldTempFiles.Count > 0)
                {
                    items.Add(new HealthItem(
                        "redundant-temp",
                        "Application Temp Files",
                        "redundant",
                        $"{oldTempFiles.Count} temporary cache files older than 7 days.",
                        tempDir,
                        size,
                        cleanable: false,
                        $"{oldTempFiles.Count} files in {tempDir}"
                    ));
                }
            }
            catch { }
        }

        return new CategoryResult(
            "redundant",
            "Software Redundant Files",
            "ملفات البرامج الزائدة عن الحاجة",
            "Clean up temp files and privacy traces generated while using the software",
            "تنظيف الملفات المؤقتة وآثار الخصوصية التي تم إنشاؤها أثناء استخدام البرنامج",
            "Rocket",
            items
        );
    }

    // 4. Software Hibernation (إسبات البرامج)
    private static CategoryResult ScanHibernation(List<InstalledApp> apps)
    {
        var items = new List<HealthItem>();
        try
        {
            var processes = Process.GetProcesses();
            var appLocations = apps
                .Where(a => !string.IsNullOrEmpty(a.InstallLocation) && Directory.Exists(a.InstallLocation))
                .ToDictionary(a => a.InstallLocation.ToLowerInvariant(), a => a);

            var seenProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in processes)
            {
                try
                {
                    if (proc.Id <= 4 || proc.ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                        proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? mainModule = null;
                    try { mainModule = proc.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(mainModule)) continue;

                    foreach (var (loc, app) in appLocations)
                    {
                        if (mainModule.ToLowerInvariant().StartsWith(loc) && seenProcs.Add(proc.ProcessName))
                        {
                            long mem = proc.WorkingSet64;
                            items.Add(new HealthItem(
                                $"hibernation-{proc.ProcessName}",
                                $"{app.Name} ({proc.ProcessName}.exe)",
                                "hibernation",
                                $"Background process is active and consuming {mem / 1024 / 1024} MB RAM.",
                                mainModule,
                                mem,
                                cleanable: false,
                                $"PID: {proc.Id} | Path: {mainModule}"
                            ));
                            break;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return new CategoryResult(
            "hibernation",
            "Software Hibernation",
            "إسبات البرامج",
            "Put software you're not using into sleep mode for a faster PC",
            "وضع البرامج التي لا تستخدمها في وضع السكون لحاسوب أسرع",
            "Moon",
            items
        );
    }

    // 5. Software Permissions (أذونات البرامج)
    private static CategoryResult ScanPermissions()
    {
        var items = new List<HealthItem>();
        try
        {
            const string consentRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
            using var rootKey = Registry.CurrentUser.OpenSubKey(consentRoot);
            if (rootKey != null)
            {
                foreach (var capName in rootKey.GetSubKeyNames())
                {
                    if (capName is "webcam" or "microphone" or "location")
                    {
                        using var capKey = rootKey.OpenSubKey(capName);
                        if (capKey == null) continue;

                        foreach (var appSub in capKey.GetSubKeyNames())
                        {
                            if (appSub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                            {
                                using var nonPackaged = capKey.OpenSubKey(appSub);
                                if (nonPackaged == null) continue;
                                foreach (var exeEntry in nonPackaged.GetSubKeyNames())
                                {
                                    items.Add(new HealthItem(
                                        $"perm-{capName}-{exeEntry}",
                                        $"{Path.GetFileName(exeEntry.Replace('#', '\\'))}",
                                        "permissions",
                                        $"Granted permission to access {capName.ToUpperInvariant()}.",
                                        exeEntry,
                                        0,
                                        cleanable: false,
                                        $"Capability: {capName}"
                                    ));
                                }
                            }
                            else
                            {
                                items.Add(new HealthItem(
                                    $"perm-{capName}-{appSub}",
                                    appSub,
                                    "permissions",
                                    $"Packaged application granted permission to access {capName.ToUpperInvariant()}.",
                                    appSub,
                                    0,
                                    cleanable: false,
                                    $"Capability: {capName}"
                                ));
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return new CategoryResult(
            "permissions",
            "Software Permissions",
            "أذونات البرامج",
            "Detect hidden permissions and disable unneeded permissions",
            "كشف الأذونات المخفية وتعطيل الأذونات غير الضرورية",
            "Lock",
            items
        );
    }

    // 6. Disturbing Notifications (الإخطارات المزعجة)
    private static CategoryResult ScanNotifications()
    {
        var items = new List<HealthItem>();
        try
        {
            const string notifRoot = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
            using var key = Registry.CurrentUser.OpenSubKey(notifRoot);
            if (key != null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub != null)
                    {
                        var enabled = sub.GetValue("Enabled");
                        if (enabled is int i && i == 1)
                        {
                            items.Add(new HealthItem(
                                $"notif-{subName}",
                                subName,
                                "notifications",
                                "App is authorized to display background toast notifications and popups.",
                                $@"{notifRoot}\{subName}",
                                0,
                                cleanable: false,
                                $"Registry: HKCU\\{notifRoot}\\{subName}"
                            ));
                        }
                    }
                }
            }
        }
        catch { }

        return new CategoryResult(
            "notifications",
            "Disturbing Notifications",
            "الإخطارات المزعجة",
            "Block popup notifications and unwanted Ads from interrupting you",
            "منع الإخطارات المنبثقة والإعلانات غير المرغوب فيها من مقاطعتك",
            "BellOff",
            items
        );
    }

    // 7. Programs with Uninstall Issues (البرامج ذات المشاكل المتعلقة بإلغاء التثبيت)
    private static CategoryResult ScanUninstallIssues(List<InstalledApp> apps)
    {
        var items = new List<HealthItem>();
        foreach (var app in apps.Where(a => a.Source == "Registry" && !a.IsSystemComponent))
        {
            if (string.IsNullOrWhiteSpace(app.UninstallString))
            {
                items.Add(new HealthItem(
                    $"issue-nocommand-{app.Id}",
                    app.Name,
                    "stubborn",
                    "No uninstall command is registered for this application.",
                    app.RegistryKeyPath,
                    0,
                    cleanable: false,
                    "Missing registry UninstallString"
                ));
                continue;
            }

            var exe = RegisteredExecutable(app.UninstallString);
            if (exe != null && !File.Exists(exe))
            {
                items.Add(new HealthItem(
                    $"issue-missingexe-{app.Id}",
                    app.Name,
                    "stubborn",
                    $"Registered uninstaller file is missing: {exe}",
                    exe,
                    0,
                    cleanable: false,
                    $"Path: {exe}"
                ));
            }
        }

        return new CategoryResult(
            "stubborn",
            "Programs with Uninstall Issues",
            "البرامج ذات المشاكل المتعلقة بإلغاء التثبيت",
            "Read-only review of registered uninstall command problems",
            "مراجعة للقراءة فقط لمشكلات أوامر الإزالة المسجلة",
            "Wrench",
            items
        );
    }

    // 8. Installation Files (ملفات التثبيت)
    private static CategoryResult ScanInstallationFiles()
    {
        var items = new List<HealthItem>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(userProfile, "Downloads");

        if (Directory.Exists(downloads))
        {
            try
            {
                foreach (var file in Directory.GetFiles(downloads, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".exe" or ".msi")
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length > 20_000_000 && (DateTime.Now - fi.LastWriteTime).TotalDays > 14) // > 20MB & > 14 days
                        {
                            items.Add(new HealthItem(
                                $"pkg-{Path.GetFileName(file)}",
                                Path.GetFileName(file),
                                "packages",
                                $"Old installer package ({fi.Length / 1024 / 1024} MB) downloaded {fi.LastWriteTime:yyyy-MM-dd}.",
                                file,
                                fi.Length,
                                cleanable: false,
                                $"File: {file} | Size: {fi.Length / 1024 / 1024} MB"
                            ));
                        }
                    }
                }
            }
            catch { }
        }

        return new CategoryResult(
            "packages",
            "Installation Files",
            "ملفات التثبيت",
            "Clean installation files to free up more disk space",
            "تنظيف ملفات التثبيت لتحرير مساحة أكبر على القرص",
            "Package",
            items
        );
    }

    // Helper: Safely calculate directory size
    private static long SafeGetDirSize(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    public static string? RegisteredExecutable(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 2) return null;
            candidate = text[1..end];
        }
        else
        {
            var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 1) return null;
            candidate = text[..(end + 4)];
        }
        return Path.IsPathFullyQualified(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? candidate : null;
    }
}
