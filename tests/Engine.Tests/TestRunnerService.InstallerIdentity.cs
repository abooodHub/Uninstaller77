using System.IO;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunInstallerIdentityTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        await RunTest("GUID Registry keys with real EXE commands remain EXE and launch the owned uninstaller", results, logger, async () =>
        {
            foreach (var misleadingMsiFlag in new[] { false, true })
            {
                using var fixture = new GuidExeFixture(misleadingMsiFlag);
                var app = RegistryDiscoveryService.ReadExactRegistration(fixture.Path)!;
                if (app.InstallerType != InstallType.StandardExe || app.HasQuietUninstall)
                    throw new Exception("GUID-shaped key or MSI flag overrode a direct EXE command.");
                var prepared = UninstallService.PrepareLaunch(app, false);
                if (prepared.IsMsi || prepared.Executable != Environment.ProcessPath || prepared.Arguments != "--fixture-uninstall-guid " + fixture.Id)
                    throw new Exception("Registered vendor EXE command was rewritten.");
                var result = await _uninstaller.UninstallAsync(app);
                if (!result.Success || !result.ExitObserved || result.ExitCode != 0 || result.Verification != VerificationStatus.VerifiedRemoved || !File.Exists(System.IO.Path.Combine(fixture.Root, "executed.json")))
                    throw new Exception("Owned GUID-key EXE did not execute and remove only its own registration.");
                using var remaining = Registry.CurrentUser.OpenSubKey(fixture.Path[5..]);
                if (remaining is not null) throw new Exception("Fixture registration remains.");
            }
            return "Two real GUID-key HKCU EXE removals, including misleading WindowsInstaller=1; actual separate child execution recorded. No Google Drive or MSI was launched.";
        });
        await RunTest("Cached MSI metadata never rewrites a direct EXE command or its arguments", results, logger, () =>
        {
            var guid = Guid.NewGuid().ToString("B");
            var args = "--uninstall --marker=msiexec /I" + guid;
            var app = new InstalledApp { InstallerType = InstallType.Msi, UninstallString = $"\"{Environment.ProcessPath}\" {args}" };
            foreach (var quiet in new[] { false, true })
            {
                var launch = UninstallService.PrepareLaunch(app, quiet);
                if (launch.IsMsi || launch.Executable != Environment.ProcessPath || launch.Arguments != args)
                    throw new Exception("MSI metadata or a substring changed the EXE launch.");
            }
            return "Both launch modes preserve EXE arguments verbatim, including msiexec text and /I-looking data.";
        });
        await RunTest("MSI commands bind only one exact product operand and reject GUIDs in unrelated properties", results, logger, () =>
        {
            var product = Guid.NewGuid().ToString("B"); var other = Guid.NewGuid().ToString("B");
            foreach (var args in new[] { "/X" + product, "/I " + product, "/x \"" + product + "\" /qn", "/uninstall " + product })
            {
                var launch = UninstallService.PrepareLaunch(new InstalledApp { UninstallString = "MsiExec.exe " + args }, false);
                if (!launch.IsMsi || launch.Executable != System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe") || launch.Arguments != "/X " + product + " /norestart")
                    throw new Exception("Valid product operand was lost.");
            }
            foreach (var args in new[] { "/X broken NOTE=" + product, "/X broken /L*v \"C:\\" + product + ".log\"", "NOTE=\" /x" + product + " \"", "/X" + product + " /I" + other })
            {
                try { UninstallService.PrepareLaunch(new InstalledApp { UninstallString = "msiexec.exe " + args }, false); }
                catch (UninstallService.LaunchValidationException ex) when (ex.Result.FailureCode == "InvalidMsiCommand" && !ex.Result.ExitObserved) { continue; }
                throw new Exception("An unrelated or ambiguous product identity was accepted.");
            }
            return "Actual Windows argument parser; only /I, /X or /uninstall product operand can select identity. No MSI process started.";
        });
        await RunTest("MSI fallback requires explicit MSI registration and absent commands; quiet-only EXE remains intact", results, logger, () =>
        {
            using var fixture = new GuidExeFixture(true);
            using (var key = Registry.CurrentUser.OpenSubKey(fixture.Path[5..], true)!)
            {
                key.DeleteValue("UninstallString");
                key.SetValue("QuietUninstallString", $"\"{Environment.ProcessPath}\" --fixture-uninstall-guid {fixture.Id}");
            }
            var quiet = RegistryDiscoveryService.ReadExactRegistration(fixture.Path)!;
            if (quiet.InstallerType != InstallType.StandardExe || UninstallService.PrepareLaunch(quiet, false).IsMsi)
                throw new Exception("Quiet-only EXE was replaced by a synthesized MSI command.");
            using (var key = Registry.CurrentUser.OpenSubKey(fixture.Path[5..], true)!) key.DeleteValue("QuietUninstallString");
            var msi = RegistryDiscoveryService.ReadExactRegistration(fixture.Path)!;
            if (msi.InstallerType != InstallType.Msi || !UninstallService.PrepareLaunch(msi, false).IsMsi)
                throw new Exception("Explicit commandless MSI registration lost its validated fallback.");
            return "Read-only preparation only; explicit MSI fallback retained without executing Windows Installer.";
        });
    }

    private sealed class GuidExeFixture : IDisposable
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string Path => @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Guid.ParseExact(Id, "N").ToString("B");
        internal string Root => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Uninstaller77_GuidExe_" + Id);
        internal GuidExeFixture(bool msiFlag)
        {
            using var previous = Registry.CurrentUser.OpenSubKey(Path[5..]);
            if (previous is not null) throw new Exception("Owned GUID collision; refusing to replace an existing registration.");
            Directory.CreateDirectory(Root); File.WriteAllText(System.IO.Path.Combine(Root, "owner.txt"), Id);
            using var key = Registry.CurrentUser.CreateSubKey(Path[5..]);
            key.SetValue("DisplayName", "Owned GUID EXE " + Id);
            key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" --fixture-uninstall-guid {Id}");
            if (msiFlag) key.SetValue("WindowsInstaller", 1);
        }
        public void Dispose() => Registry.CurrentUser.DeleteSubKey(Path[5..], false);
    }
}
