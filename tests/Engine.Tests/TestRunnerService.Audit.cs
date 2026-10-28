using System.IO;
using System.Text.Json;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunAuditTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        await RunTest("Audit malformed extension manifests cannot erase a valid neighboring extension", results, logger, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "U77_Audit_Manifest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Manifest(root, 'a', """{"name":"Valid fixture","version":"1.0"}""");
                Manifest(root, 'b', """{"name":7,"version":"1.0"}""");
                Manifest(root, 'c', "[]");
                var items = BrowserExtensionInventoryService.ScanRoots([("Fixture Browser", root)]);
                if (items.Count != 1 || items[0].Name != "Valid fixture") throw new Exception("Malformed manifest displaced the valid fixture.");
                return "One valid extension retained; malformed root/metadata isolated without losing the scan.";
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        await RunTest("Audit explicit disable reasons take precedence over stale enabled state", results, logger, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "U77_Audit_State_" + Guid.NewGuid().ToString("N"));
            try
            {
                Manifest(root, 'a', """{"name":"Disabled fixture","version":"1.0"}""");
                File.WriteAllText(Path.Combine(root, "Default", "Secure Preferences"), JsonSerializer.Serialize(new
                {
                    extensions = new { settings = new Dictionary<string, object> { [new string('a', 32)] = new { state = 1, disable_reasons = new[] { 4 } } } }
                }));
                var item = BrowserExtensionInventoryService.ScanRoots([("Fixture Browser", root)]).Single();
                if (item.EnabledState != "Disabled") throw new Exception("An explicit disable reason was reported as " + item.EnabledState);
                return "Disabled evidence wins over an inconsistent cached enabled field; profile bytes never modified.";
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        await RunTest("Audit removed UI commands cannot be invoked through the bridge", results, logger, () =>
        {
            foreach (var operation in new[] { "manageExtensionInBrowser", "toggleView", "setReduceMotion", "scanHealth", "reviewPreviousHealth", "showLatestHealth" })
                if (WebUiProtocol.TryParse(JsonSerializer.Serialize(new { id = "audit", op = operation, payload = new { } }), out _, out _))
                    throw new Exception("Removed command remains reachable: " + operation);
            return "Removed actions rejected without launching any tool or changing settings.";
        });
        await RunTest("Audit Chromium numbered force-list and JSON policies are read from an isolated key", results, logger, () =>
        {
            var path = @"Software\Uninstaller77_Test_Policy_" + Guid.NewGuid().ToString("N");
            var id = new string('a', 32);
            try
            {
                using var policy = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path);
                using (var force = policy.CreateSubKey("ExtensionInstallForcelist")) force.SetValue("1", id + ";https://fixture.invalid/update");
                if (!BrowserExtensionInventoryService.ReadPolicyKey(policy, id)) throw new Exception("Numbered force-install policy was missed.");
                if (BrowserExtensionInventoryService.ReadPolicyKey(policy, new string('b', 32))) throw new Exception("Unrelated extension received a managed claim.");
                policy.DeleteSubKeyTree("ExtensionInstallForcelist");
                policy.SetValue("ExtensionSettings", JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { installation_mode = "force_installed" } }));
                if (!BrowserExtensionInventoryService.ReadPolicyKey(policy, id)) throw new Exception("JSON policy was missed.");
                policy.SetValue("ExtensionSettings", "invalid-json");
                var errors = 0;
                if (BrowserExtensionInventoryService.ReadPolicyKey(policy, id, _ => errors++) || errors != 1) throw new Exception("Malformed policy was not diagnosed safely.");
                return "Actual Windows registry representations recognized without touching any browser policy.";
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        });
        await RunTest("Audit malformed manifests retain diagnostics and do not masquerade as an empty clean scan", results, logger, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "U77_Audit_Diagnostic_" + Guid.NewGuid().ToString("N"));
            try
            {
                Manifest(root, 'a', "{bad-json");
                var diagnostics = new List<BrowserExtensionInventoryService.ScanDiagnostic>();
                var items = BrowserExtensionInventoryService.ScanRoots([("Fixture Browser", root)], diagnostics.Add);
                if (items.Count != 0 || diagnostics.Count != 1 || diagnostics[0].Stage != "Manifest") throw new Exception("Incomplete inventory lost its error evidence.");
                return "No invented extension, and one explicit malformed-manifest diagnostic retained.";
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        await RunTest("Audit AppX query pins the system executable and UTF-8 instead of PATH lookup", results, logger, () =>
        {
            var command = RegistryDiscoveryService.CreateMsixQueryStartInfo();
            var expected = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (command.FileName != expected || command.UseShellExecute || command.StandardOutputEncoding?.WebName != "utf-8" ||
                !command.Arguments.Contains("-ErrorAction Stop", StringComparison.Ordinal)) throw new Exception("AppX query does not use a pinned UTF-8 read-only command.");
            if (WebUiProtocol.IsTrustedSource("https://spoof@appassets.winuninstaller/index.html")) throw new Exception("A credential-bearing app URI was trusted.");
            return "System PowerShell pinned, UTF-8 decoding explicit, errors stop the query, and credential-bearing bridge origins rejected.";
        });
    }

    private static void Manifest(string root, char id, string json)
    {
        var directory = Path.Combine(root, "Default", "Extensions", new string(id, 32), "1.0_0");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), json);
    }
}
