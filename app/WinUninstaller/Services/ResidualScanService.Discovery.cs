using System.IO;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class ResidualScanService
{
    // Discovery is read-only. A name or path association is evidence for review,
    // never proof of exclusive ownership or permission to delete.
    private static void AddExpandedInformationalMatches(InstalledApp app, List<ResidualItem> items,
        IProgress<ScanProgress>? progress, int startingCount, bool deep, CancellationToken ct)
    {
        var fullName = app.Name.Trim();
        if (!SafeSegment(fullName)) return;
        var publisher = app.Publisher.Trim();
        foreach (var suffix in new[] { " Corporation", " Incorporated", " Inc.", " Inc", " LLC", " Ltd." })
            if (publisher.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                publisher = publisher[..^suffix.Length].Trim();
        if (!SafeSegment(publisher)) publisher = "";
        var product = publisher.Length > 0 && fullName.StartsWith(publisher + " ", StringComparison.OrdinalIgnoreCase)
            ? fullName[(publisher.Length + 1)..].Trim() : fullName;
        if (!SafeSegment(product)) product = fullName;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullName, product };
        var qualifier = product.IndexOf(" (", StringComparison.Ordinal);
        if (qualifier >= 5 && product.EndsWith(')'))
        {
            product = product[..qualifier];
            names.Add(product);
        }
        if (product.Contains(' ') && SafeSegment(product.Replace(" ", "", StringComparison.Ordinal)))
            names.Add(product.Replace(" ", "", StringComparison.Ordinal));
        var vendorNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var firstWord = product.Split(' ', 2)[0];
        if (publisher.Length > 0 && SafeSegment(firstWord) && firstWord.Length >= 5)
            vendorNames.Add(firstWord);

        var scanned = startingCount;
        var seen = new HashSet<string>(items.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        void Add(ResidualType type, string path, string reason)
        {
            if (seen.Add(path)) items.Add(new ResidualItem
            {
                Type = type, Path = path, Confidence = ConfidenceLevel.Low,
                Reason = reason, CanClean = false, IsSelected = false
            });
        }

        var profiles = deep ? AccessibleProfileRoots(reason =>
            progress?.Report(new("Checking accessible user profiles", scanned, UnavailableReason: reason))).Take(64).ToArray()
            : [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var profile in profiles)
        {
            folders.Add(Path.Combine(profile, "AppData", "Local"));
            folders.Add(Path.Combine(profile, "AppData", "Roaming"));
            folders.Add(Path.Combine(profile, "AppData", "LocalLow"));
            if (SafeSegment(app.PackageFamilyName))
            {
                var packageFolder = Path.Combine(profile, "AppData", "Local", "Packages", app.PackageFamilyName);
                try
                {
                    if (Directory.Exists(packageFolder)) Add(ResidualType.Directory, packageFolder,
                        "Exact package-family folder; may contain personal app data and is review-only.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            foreach (var name in names)
            {
                AddFolder(Path.Combine(folder, name));
            }
            if (publisher.Length > 0)
                foreach (var name in vendorNames) AddFolder(Path.Combine(folder, publisher, name));
            progress?.Report(new("Checking bounded application folders", scanned));
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in hive == RegistryHive.CurrentUser
                     ? new[] { RegistryView.Registry64 } : new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                var prefix = hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\";
                foreach (var name in names)
                    AddRegistry(root, "Software\\" + name);
                if (publisher.Length > 0)
                    foreach (var name in vendorNames) AddRegistry(root, "Software\\" + publisher + "\\" + name);
                // Another registry view can retain the original uninstall identity.
                if (app.RegistryKeyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    AddRegistry(root, app.RegistryKeyPath[prefix.Length..], ResidualType.InstallationInfo);
                scanned++;
                progress?.Report(new("Checking 32-bit and 64-bit Registry views", scanned));

                void AddRegistry(RegistryKey baseKey, string relativePath, ResidualType type = ResidualType.RegistryKey)
                {
                    if (view == RegistryView.Registry64 && seen.Contains(prefix + relativePath)) return;
                    using var key = baseKey.OpenSubKey(relativePath);
                    if (key is null) return;
                    Add(type, prefix + relativePath + (view == RegistryView.Registry32 ? " [32-bit]" : " [64-bit]"),
                        type == ResidualType.InstallationInfo
                            ? "Recorded uninstall identity remains in this Registry view; verify the registration before acting."
                            : "Product-name path match in this Registry view; shared ownership is unverified.");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { progress?.Report(new("Checking 32-bit and 64-bit Registry views", scanned, UnavailableReason: ex.GetType().Name)); }
        }

        if (deep)
        {
            AddServices();
            AddTasks();
            AddShortcuts();
        }

        void AddFolder(string path)
        {
            try
            {
                if (Directory.Exists(path) && !SamePath(path, app.InstallLocation) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    Add(ResidualType.Directory, path,
                        "Product or publisher/name path match only; ownership and user data are unverified.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        void AddServices()
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (services is null) return;
                var installRoot = app.InstallLocation.TrimEnd('\\', '/');
                if (!Path.IsPathFullyQualified(installRoot) || IsReservedPath(installRoot)) return;
                foreach (var name in services.GetSubKeyNames().Take(4096))
                {
                    ct.ThrowIfCancellationRequested();
                    using var service = services.OpenSubKey(name);
                    var image = Environment.ExpandEnvironmentVariables(service?.GetValue("ImagePath") as string ?? "");
                    if (image.Contains(installRoot + "\\", StringComparison.OrdinalIgnoreCase))
                        Add(ResidualType.Service, @"HKLM\SYSTEM\CurrentControlSet\Services\" + name,
                            "Service image refers to the recorded former install location; service ownership requires review.");
                }
                progress?.Report(new("Checking service registrations", ++scanned));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { progress?.Report(new("Checking service registrations", scanned, UnavailableReason: ex.GetType().Name)); }
        }

        void AddTasks()
        {
            ct.ThrowIfCancellationRequested();
            var taskRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "Tasks");
            if (!Directory.Exists(taskRoot)) return;
            foreach (var name in names)
            {
                AddTask(Path.Combine(taskRoot, name));
                if (publisher.Length > 0) AddTask(Path.Combine(taskRoot, publisher, name));
            }
            if (publisher.Length > 0)
            {
                var vendorTasks = Path.Combine(taskRoot, publisher);
                try
                {
                    if (Directory.Exists(vendorTasks))
                        foreach (var path in Directory.EnumerateFiles(vendorTasks, "*", SearchOption.TopDirectoryOnly).Take(256))
                            if (vendorNames.Any(name => Path.GetFileName(path).StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)))
                                AddTask(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { progress?.Report(new("Checking named scheduled tasks", scanned, UnavailableReason: ex.GetType().Name)); }
            }
            progress?.Report(new("Checking named scheduled tasks", ++scanned));
        }

        void AddTask(string path)
        {
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    Add(ResidualType.ScheduledTask, path,
                        "Scheduled-task name matches the product identity; its actions and ownership are unverified.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { progress?.Report(new("Checking named scheduled tasks", scanned, UnavailableReason: ex.GetType().Name)); }
        }

        void AddShortcuts()
        {
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
            };
            foreach (var profile in profiles)
            {
                roots.Add(Path.Combine(profile, "Desktop"));
                roots.Add(Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"));
            }
            foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                ScanShortcutFolder(root);
                if (publisher.Length > 0) ScanShortcutFolder(Path.Combine(root, publisher));
                progress?.Report(new("Checking desktop and Start menu shortcuts", ++scanned));
            }
        }

        void ScanShortcutFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var link in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly).Take(512))
                {
                    ct.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0 || new FileInfo(link).Length > 1024 * 1024) continue;
                    var label = Path.GetFileNameWithoutExtension(link);
                    var nameMatches = names.Any(name => label.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var target = ShellShortcutFile.ReadTarget(link);
                    var targetMatches = target is not null && Path.IsPathFullyQualified(app.InstallLocation) &&
                        target.StartsWith(app.InstallLocation.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
                    if (nameMatches || targetMatches)
                        Add(ResidualType.Shortcut, link, targetMatches
                            ? "Shortcut target refers to the recorded former install location; review before removal."
                            : "Shortcut name matches the product only; target and ownership are unverified.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { progress?.Report(new("Checking desktop and Start menu shortcuts", scanned, UnavailableReason: ex.GetType().Name)); }
        }
    }

    private static IEnumerable<string> AccessibleProfileRoots(Action<string> unavailable)
    {
        var current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(current)) yield return current;
        string[] profiles;
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            profiles = list?.GetSubKeyNames().Take(64).Select(sid =>
            {
                using var key = list.OpenSubKey(sid);
                return Environment.ExpandEnvironmentVariables(key?.GetValue("ProfileImagePath") as string ?? "");
            }).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { profiles = []; unavailable("ProfileList: " + ex.GetType().Name); }
        foreach (var profile in profiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(profile) || profile.StartsWith(@"\\", StringComparison.Ordinal) ||
                profile.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
            bool accessible;
            try { accessible = Directory.Exists(profile) && (File.GetAttributes(profile) & FileAttributes.ReparsePoint) == 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { accessible = false; unavailable(profile + ": " + ex.GetType().Name); }
            if (accessible) yield return profile;
        }
    }

    private static bool SafeSegment(string value) => value.Length >= 3 && value.Length <= 100 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.EndsWith('.') && !value.EndsWith(' ');
}
