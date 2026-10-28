using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunBrokerTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        const string uninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
        var executable = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
        static string CreateFixture(string name, string command)
        {
            var path = uninstallRoot + name;
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("DisplayName", "Owned broker test " + name); key.SetValue("UninstallString", command);
            return "HKCU\\" + path;
        }
        static void RemoveFixture(string name) => Registry.CurrentUser.DeleteSubKeyTree(uninstallRoot + name, false);

        await RunTest("Authenticated worker tracks real early-exit launcher and child", results, logger, async () =>
        {
            var name = "WinUninstaller_Test_" + Guid.NewGuid().ToString("N");
            var path = CreateFixture(name, $"\"{executable}\" --fixture-child 1800");
            try
            {
                using var session = await UninstallBroker.StartAsync(path, false, executable, "--fixture-child 1800", elevate: false);
                using var worker = session.Process;
                if (!session.TrackingStarted || session.ChildId is null || session.ChildId == worker.Id) throw new Exception("No distinct tracked child was authenticated.");
                if (await session.WaitAsync(TimeSpan.FromMilliseconds(50)) || session.TreeComplete || session.ExitCode is not null)
                    throw new Exception("Early launcher exit was misreported as complete.");
                if (!await session.WaitAsync(TimeSpan.FromSeconds(10)) || !session.TreeComplete || session.ExitCode != 0)
                    throw new Exception("The completed child tree remained stuck: " + session.Error);
                return "Real worker IPC, real Windows job and real descendant lifetime; no elevation, user program or mock completion.";
            }
            finally { RemoveFixture(name); }
        });
        await RunTest("Authenticated worker runs actual owned fixture uninstaller", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N"); var name = "WinUninstaller_Test_" + id;
            var args = "--fixture-uninstall " + id;
            var path = CreateFixture(name, $"\"{executable}\" {args}");
            try
            {
                using var session = await UninstallBroker.StartAsync(path, false, executable, args, elevate: false);
                using var worker = session.Process;
                if (!await session.WaitAsync(TimeSpan.FromSeconds(10)) || session.ExitCode != 0) throw new Exception("Owned uninstaller did not complete: " + session.Error);
                using var key = Registry.CurrentUser.OpenSubKey(uninstallRoot + name);
                if (key is not null) throw new Exception("The actual fixture registration was not removed.");
                return "Authenticated worker executed test EXE; only its GUID-bound HKCU registration disappeared, actual exit 0 retained.";
            }
            finally { RemoveFixture(name); }
        });
        await RunTest("Authenticated worker preserves actual failure exit instead of its own exit", results, logger, async () =>
        {
            var name = "WinUninstaller_Test_" + Guid.NewGuid().ToString("N");
            var path = CreateFixture(name, $"\"{executable}\" --fixture-exit 1603");
            try
            {
                using var session = await UninstallBroker.StartAsync(path, false, executable, "--fixture-exit 1603", elevate: false);
                using var worker = session.Process;
                if (!await session.WaitAsync(TimeSpan.FromSeconds(10)) || session.ExitCode != 1603) throw new Exception("Worker replaced the actual uninstaller exit code.");
                var outcome = UninstallService.InterpretCompletedProcessResult(session.ExitCode.Value, VerificationStatus.StillPresent, false);
                if (outcome.Success || BatchUninstallCoordinator.Classify(outcome) != BatchItemState.Failed) throw new Exception("Real failure became a success.");
                return "Actual child exit 1603 preserved across IPC; worker exit 0 never becomes a successful uninstall.";
            }
            finally { RemoveFixture(name); }
        });
        await RunTest("Worker rejects changed registered command before launching", results, logger, async () =>
        {
            var name = "WinUninstaller_Test_" + Guid.NewGuid().ToString("N");
            var path = CreateFixture(name, $"\"{executable}\" --fixture-delay 100");
            try
            {
                try
                {
                    using var session = await UninstallBroker.StartAsync(path, false, executable, "--fixture-child 1800", elevate: false);
                    using var worker = session.Process;
                    throw new Exception("A stale command was accepted.");
                }
                catch (UninstallBroker.BrokerRejectedException) { }
                return "Real authenticated worker independently read the registration and rejected mismatched arguments without child launch.";
            }
            finally { RemoveFixture(name); }
        });
        await RunTest("Worker rejects arbitrary paths, malformed identities and command hosts", results, logger, () =>
        {
            foreach (var path in new[] { "", "HKCU\\Software\\Other", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\App\Child", "C:\\uninstall.exe" })
                if (UninstallBroker.IsRegistrationPath(path)) throw new Exception("Unsupported worker identity accepted.");
            var name = "WinUninstaller_Test_" + Guid.NewGuid().ToString("N");
            var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var pathName = CreateFixture(name, $"\"{shell}\" /c exit 0");
            try
            {
                try { UninstallBroker.ValidateRequest(new(pathName, false, shell, "/c exit 0")); throw new Exception("Shell command accepted."); }
                catch (UninstallService.LaunchValidationException) { }
                return "Exact uninstall-key identity required; arbitrary paths, nested keys and registered shell hosts rejected.";
            }
            finally { RemoveFixture(name); }
        });
        await RunTest("Worker messages reject oversized, duplicate and unknown fields", results, logger, async () =>
        {
            foreach (var body in new[] { "{\"State\":\"Completed\",\"State\":\"Ready\"}", "{\"State\":\"Ready\",\"path\":\"C:\\\\anything\"}", "null", "[1]" })
            {
                using var stream = new MemoryStream(); var bytes = Encoding.UTF8.GetBytes(body);
                var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length); stream.Write(size); stream.Write(bytes); stream.Position = 0;
                try { await UninstallBroker.ReadAsync<UninstallBroker.Message>(stream, CancellationToken.None); throw new Exception("Malformed message accepted."); }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { }
            }
            foreach (var length in new[] { -1, 0, 32769 })
            {
                var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, length); using var stream = new MemoryStream(size);
                try { await UninstallBroker.ReadAsync<UninstallBroker.Message>(stream, CancellationToken.None); throw new Exception("Invalid frame length accepted."); }
                catch (InvalidDataException) { }
            }
            if (await UninstallBroker.RunAsync([UninstallBroker.Switch, "untrusted-pipe", Environment.ProcessId.ToString()]) != 2)
                throw new Exception("Arbitrary CLI pipe accepted.");
            return "Length limits, exact fields, unique fields and random pipe identity enforced before launch.";
        });
        await RunTest("Delayed registration verification retains actual exit across repeated retries", results, logger, async () =>
        {
            var name = "WinUninstaller_Test_" + Guid.NewGuid().ToString("N");
            var path = CreateFixture(name, $"\"{executable}\" --fixture-delay 900");
            var app = new InstalledApp { Id = name, Name = "Owned delayed-registration fixture", RegistryHive = "HKCU", RegistryKeyPath = path,
                Source = "Registry", UninstallString = $"\"{executable}\" --fixture-delay 900" };
            var engine = new UninstallService(TimeSpan.FromMilliseconds(50));
            try
            {
                var first = await engine.UninstallAsync(app);
                if (first.Verification != VerificationStatus.Unknown || first.Success) throw new Exception("A running fixture was finalized.");
                await Task.Delay(1200);
                var pendingRegistration = await engine.RetryVerificationAsync(app);
                if (pendingRegistration.Verification != VerificationStatus.StillPresent || pendingRegistration.Success || pendingRegistration.ExitCode != 0 || UninstallService.HasPendingOperation)
                    throw new Exception("Completed launcher with unchanged registration lost its exit evidence.");
                RemoveFixture(name);
                var resolved = await engine.RetryVerificationAsync(app);
                if (!resolved.Success || resolved.ExitCode != 0 || resolved.Verification != VerificationStatus.VerifiedRemoved || UninstallService.HasPendingOperation)
                    throw new Exception("Repeated retry remained stuck after exact owned registration disappeared.");
                return "Actual process timed out without termination, actual exit 0 retained through an unchanged registration, later exact absence resolved the same attempt.";
            }
            finally { RemoveFixture(name); }
        });
    }
}
