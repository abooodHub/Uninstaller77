using WinUninstaller.Models;
using WinUninstaller.Services;

internal static class UninstallPolicyChecks
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool passed, string label)
        {
            if (!passed) throw new InvalidOperationException(label);
            checks++;
            Console.WriteLine("PASS " + label);
        }

        var blocked = new InstalledApp { Id = "blocked", Name = "Owned protected fixture", IsRemovalBlocked = true,
            UninstallString = "MsiExec.exe /X{EB123185-40CE-431D-AB98-123456789ABC}" };
        foreach (var quiet in new[] { false, true })
        {
            try
            {
                _ = RegisteredUninstallCommand.PrepareLaunch(blocked, quiet);
                Check(false, "Blocked registration cannot prepare a launch");
            }
            catch (RegisteredUninstallCommand.LaunchValidationException error)
            {
                Check(!blocked.CanUninstall && error.FailureCode == "RemovalBlocked", "NoRemove blocks launch in quiet and interactive modes");
            }
        }

        var supported = new InstalledApp { Id = "supported", Name = "Microsoft fixture", Version = "1",
            UninstallString = blocked.UninstallString };
        Check(RegisteredUninstallCommand.PrepareLaunch(supported, false).IsMsi, "Removable MSI still uses Windows Installer");
        Check(UninstallService.SameMsiRetryIdentity(supported, supported), "Unchanged installation remains eligible for retry");
        var replacement = new InstalledApp { Name = supported.Name, Version = "2" };
        Check(!UninstallService.SameMsiRetryIdentity(supported, replacement), "Version change invalidates the MSI retry");
        Check(UninstallService.ShouldRetryMsi(true, 1618, false, false), "Busy MSI is eligible for one retry");
        Check(!UninstallService.ShouldRetryMsi(true, 1618, true, false), "Busy MSI cannot retry twice");
        Check(!UninstallService.ShouldRetryMsi(true, 1618, false, true), "Stop request prevents retry");
        Check(!UninstallService.ShouldRetryMsi(false, 1618, false, false), "Vendor EXE exit 1618 does not trigger retry");

        foreach (var (code, diagnostic) in new[] { (1603, "MsiInstallFailure"), (1612, "MsiSourceUnavailable"),
            (1618, "MsiBusy"), (1619, "MsiSourceUnavailable"), (1625, "MsiPolicyBlocked"), (1722, "MsiInstallFailure") })
        {
            var result = UninstallService.InterpretCompletedProcessResult(code, VerificationStatus.StillPresent, true);
            Check(!result.Success && result.ObservedExitCode == code && result.FailureCode == diagnostic &&
                UninstallService.ShouldRetryMsi(true, code, false, false) == (code == 1618), $"MSI exit {code} retains its actual diagnosis");
            Check(UninstallService.InterpretCompletedProcessResult(code, VerificationStatus.StillPresent, false).FailureCode ==
                "UninstallerExitError", $"Vendor EXE exit {code} retains its own meaning");
        }

        var cancelled = await BatchUninstallCoordinator.RunAsync([supported, blocked],
            _ => Task.FromResult(UninstallService.InterpretCompletedProcessResult(1602, VerificationStatus.StillPresent, true)), () => false);
        Check(cancelled.Items[0].State == BatchItemState.Cancelled && cancelled.Items[0].ExitCode == 1602 &&
            cancelled.Items[1].State == BatchItemState.Skipped, "Windows Installer cancellation stops the queue and preserves the observed exit");
        Console.WriteLine($"RESULT {checks} passed / 0 failed; no uninstallers launched");
        return 0;
    }
}
