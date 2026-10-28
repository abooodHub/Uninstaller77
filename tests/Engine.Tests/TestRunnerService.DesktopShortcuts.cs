using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunDesktopShortcutTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        await RunTest("Desktop cleanup deletes only captured missing-target shortcuts after verified removal", results, logger, () =>
        {
            using var fixture = new DesktopShortcutFixture();
            var first = fixture.Link("تجربة برنامج.lnk", fixture.Target);
            var second = fixture.Link("Shared test desktop.lnk", fixture.Target, shared: true);
            var outside = fixture.Link("Unrelated.lnk", fixture.OtherTarget);
            var broken = fixture.Link("Already broken.lnk", Path.Combine(fixture.Root, "NeverExisted.exe"));
            File.WriteAllText(Path.Combine(fixture.Desktop, "notes.txt"), "Owned note, preserve");
            var snapshot = fixture.Service.Capture(fixture.App);
            if (snapshot.Candidates.Length != 2) throw new Exception("Wrong captured scope.");
            fixture.RemoveTargetAndRegistration();
            var result = fixture.Service.Complete(snapshot, true, VerificationStatus.VerifiedRemoved);
            if (result.Removed != 2 || result.Kept != 0 || File.Exists(first) || File.Exists(second) || !File.Exists(outside) || !File.Exists(broken) || !File.Exists(fixture.OtherTarget))
                throw new Exception("Eligible links not deleted, or unrelated/broken links were touched.");
            return "Two actual .lnk files in isolated desktop fixtures removed; unrelated target/link, pre-existing broken link and notes preserved. No real desktop was scanned or modified.";
        });
        await RunTest("Desktop cleanup retains shortcuts on failed or uncertain removal, present registration or live target", results, logger, () =>
        {
            using var fixture = new DesktopShortcutFixture();
            var link = fixture.Link("Keep.lnk", fixture.Target);
            var snapshot = fixture.Service.Capture(fixture.App);
            File.Delete(fixture.Target);
            foreach (var state in new[] { (false, VerificationStatus.VerifiedRemoved), (true, VerificationStatus.Unknown), (true, VerificationStatus.StillPresent), (true, VerificationStatus.VerifiedRemoved) })
                if (fixture.Service.Complete(snapshot, state.Item1, state.Item2).Removed != 0 || !File.Exists(link)) throw new Exception("Unverified removal deleted a link.");
            fixture.RemoveRegistration(); File.WriteAllText(fixture.Target, "Restored target fixture");
            if (fixture.Service.Complete(snapshot, true, VerificationStatus.VerifiedRemoved).Removed != 0 || !File.Exists(link)) throw new Exception("Live target link deleted.");
            return "Failure, unknown/present registration, and still-existing target never authorize shortcut deletion.";
        });
        await RunTest("Desktop cleanup preserves changed and retargeted shortcuts and links held open without delete sharing", results, logger, () =>
        {
            using var fixture = new DesktopShortcutFixture();
            var changed = fixture.Link("Changed.lnk", fixture.Target);
            var retargeted = fixture.Link("Retargeted.lnk", fixture.Target);
            var locked = fixture.Link("Locked.lnk", fixture.Target);
            var snapshot = fixture.Service.Capture(fixture.App);
            fixture.Link("Changed.lnk", fixture.Target, description: "Changed after capture");
            fixture.Link("Retargeted.lnk", fixture.OtherTarget);
            fixture.RemoveTargetAndRegistration();
            using var pin = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = fixture.Service.Complete(snapshot, true, VerificationStatus.VerifiedRemoved);
            if (result.Removed != 0 || result.Kept != 3 || !File.Exists(changed) || !File.Exists(retargeted) || !File.Exists(locked)) throw new Exception("Changed or locked link was deleted.");
            return "Original fingerprint/target and exclusive pinned link identity are required; changed, retargeted and locked fixtures were kept.";
        });
        await RunTest("Desktop cleanup rejects broad installation roots and limits missing-location fallback to the exact icon target", results, logger, () =>
        {
            using var fixture = new DesktopShortcutFixture();
            fixture.Link("Owned.lnk", fixture.Target);
            var sibling = Path.Combine(Path.GetDirectoryName(fixture.Target)!, "Sibling.exe"); File.WriteAllText(sibling, "Owned sibling fixture");
            fixture.Link("Sibling.lnk", sibling);
            var original = fixture.App.InstallLocation;
            fixture.App.InstallLocation = Path.GetTempPath(); fixture.App.DisplayIcon = "";
            if (fixture.Service.Capture(fixture.App).Candidates.Length != 0) throw new Exception("Shared temporary root accepted.");
            fixture.App.InstallLocation = ""; fixture.App.DisplayIcon = fixture.Target;
            if (fixture.Service.Capture(fixture.App).Candidates.Length != 1) throw new Exception("Icon fallback inferred a whole folder.");
            fixture.App.InstallLocation = original;
            return "Shared root rejected; icon fallback authorizes only the exact EXE target, never sibling executables.";
        });
        await RunTest("Desktop cleanup treats vendor-removed links as already gone without false cleanup credit or warning", results, logger, () =>
        {
            using var fixture = new DesktopShortcutFixture();
            var link = fixture.Link("Vendor removed.lnk", fixture.Target);
            var snapshot = fixture.Service.Capture(fixture.App);
            fixture.RemoveTargetAndRegistration(); File.Delete(link);
            var result = fixture.Service.Complete(snapshot, true, VerificationStatus.VerifiedRemoved);
            if (result.Removed != 0 || result.Kept != 0) throw new Exception("Already removed vendor link falsely counted.");
            return "No invented deletion count or false retained-link warning when the vendor already removed the shortcut.";
        });
        await RunTest("Customized profile and whitespace-argument desktop links are never cleanup candidates", results, logger, () =>
        {
            using var fixture = new DesktopShortcutFixture();
            var normal = fixture.Link("Normal.lnk", fixture.Target);
            var customized = fixture.Link("Profile.lnk", fixture.Target, arguments: "--profile-directory=Owned");
            var whitespace = fixture.Link("Whitespace.lnk", fixture.Target, arguments: "   ");
            var snapshot = fixture.Service.Capture(fixture.App);
            if (snapshot.Candidates.Length != 1 || ShellShortcutFile.Read(customized)?.Arguments != "--profile-directory=Owned")
                throw new Exception("Customized shortcut captured or COM arguments lost.");
            fixture.RemoveTargetAndRegistration();
            var result = fixture.Service.Complete(snapshot, true, VerificationStatus.VerifiedRemoved);
            if (result.Removed != 1 || File.Exists(normal) || !File.Exists(customized) || !File.Exists(whitespace))
                throw new Exception("Customized shortcut deleted.");
            return "Real Unicode ShellLink COM round-trip; only argument-free link removed in isolated desktops.";
        });
        await RunTest("Actual owned EXE uninstall removes its target and registration then cleans its isolated desktop shortcut", results, logger, async () =>
        {
            using var fixture = new DesktopShortcutFixture();
            var link = fixture.Link("Real uninstall.lnk", fixture.Target);
            var engine = new UninstallService(TimeSpan.FromSeconds(10), fixture.Service);
            var result = await engine.UninstallAsync(fixture.App);
            if (!result.Success || result.ExitCode != 0 || result.Verification != VerificationStatus.VerifiedRemoved || File.Exists(link) || File.Exists(fixture.Target) || !File.Exists(Path.Combine(fixture.Root, "execution.txt")))
                throw new Exception("Live engine did not clean the exact link after the real owned uninstaller.");
            return "Real separately launched EXE removed only its fixture target and GUID HKCU key. Live engine then deleted the exact unchanged .lnk in the isolated fixture desktop; no real application/desktop touched.";
        });
    }

    private sealed class DesktopShortcutFixture : IDisposable
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string Root { get; }
        internal string Desktop { get; }
        internal string SharedDesktop { get; }
        internal string Target { get; }
        internal string OtherTarget { get; }
        internal InstalledApp App { get; }
        internal DesktopShortcutCleanupService Service { get; }
        internal DesktopShortcutFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "Uninstaller77_DesktopShortcut_" + Id);
            Desktop = Path.Combine(Root, "Owned Desktop"); SharedDesktop = Path.Combine(Root, "Owned Shared Desktop");
            Target = Path.Combine(Root, "App", "Owned.exe"); OtherTarget = Path.Combine(Root, "Other", "Other.exe");
            foreach (var dir in new[] { Desktop, SharedDesktop, Path.GetDirectoryName(Target)!, Path.GetDirectoryName(OtherTarget)! }) Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(Root, "owner.txt"), Id); File.WriteAllText(Target, "Owned EXE target fixture"); File.WriteAllText(OtherTarget, "Unrelated owned target, preserve");
            App = new() { Id = Id, Name = "Owned shortcut uninstall fixture", Source = "Registry", RegistryHive = "HKCU", InstallLocation = Path.GetDirectoryName(Target)!, DisplayIcon = Target,
                RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + Id,
                UninstallString = $"\"{Environment.ProcessPath}\" --fixture-uninstall-shortcut {Id}" };
            using var key = Registry.CurrentUser.CreateSubKey(App.RegistryKeyPath[5..]); key.SetValue("DisplayName", App.Name); key.SetValue("InstallLocation", App.InstallLocation); key.SetValue("DisplayIcon", App.DisplayIcon); key.SetValue("UninstallString", App.UninstallString);
            Service = new([Desktop, SharedDesktop]);
        }
        internal string Link(string name, string target, bool shared = false, string description = "Original owned shortcut", string arguments = "")
        {
            var path = Path.Combine(shared ? SharedDesktop : Desktop, name);
            object? link = null;
            try
            {
                link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellShortcutFile.ClassId, true)!);
                var item = (ShellShortcutFile.Link)link!;
                item.SetPath(target); item.SetArguments(arguments); item.SetDescription(description); ((IPersistFile)link!).Save(path, true);
                return path;
            }
            finally { if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); }
        }
        internal void RemoveRegistration() => Registry.CurrentUser.DeleteSubKeyTree(App.RegistryKeyPath[5..], false);
        internal void RemoveTargetAndRegistration() { File.Delete(Target); RemoveRegistration(); }
        public void Dispose() => RemoveRegistration();
    }
}
