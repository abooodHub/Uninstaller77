using System.Text;
using WinUninstaller.Services;

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "U77 Policy Fixture");
var identity = new RegistryResidualIdentity("Owned Product", "Example Vendor", root,
    @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\{22222222-2222-2222-2222-222222222222}", "64-bit");
var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["InstallLocation"] = root, ["UninstallRegistryKey"] = identity.RegistrationPath,
    ["DisplayName"] = identity.Name, ["Publisher"] = identity.Publisher
};
var snapshot = new RegistryResidualSnapshot(RegistryResidualPolicy.ExpectedPath(identity), 64, values, 0, true);
Check(RegistryResidualPolicy.Evaluate(identity, snapshot) == RegistryResidualKind.Key, "explicit metadata key");
var evidence = new RegistryResidualEvidence(identity, snapshot, RegistryResidualKind.Key,
    RegistryResidualPolicy.Fingerprint(snapshot));
var keyBackup = Encoding.Unicode.GetString(RegistryResidualPolicy.BackupReg(evidence));
Check(keyBackup.StartsWith("\uFEFFWindows Registry Editor Version 5.00", StringComparison.Ordinal), "REG header and encoding");
Check(keyBackup.Contains("[HKEY_LOCAL_MACHINE\\Software\\Example Vendor\\Owned Product]", StringComparison.Ordinal), "exact key path");
Check(keyBackup.Contains("\"UninstallRegistryKey\"=", StringComparison.Ordinal), "key identity backup");

var withUserData = snapshot with { Values = new Dictionary<string, string>(values) { ["UserPreference"] = "private" } };
Check(RegistryResidualPolicy.Evaluate(identity, withUserData) == RegistryResidualKind.Value, "shared key allows one explicit value only");
var valueEvidence = new RegistryResidualEvidence(identity, withUserData, RegistryResidualKind.Value,
    RegistryResidualPolicy.Fingerprint(withUserData));
var valueBackup = Encoding.Unicode.GetString(RegistryResidualPolicy.BackupReg(valueEvidence));
Check(valueBackup.Contains("\"InstallLocation\"=", StringComparison.Ordinal) &&
    !valueBackup.Contains("UserPreference", StringComparison.Ordinal) &&
    !valueBackup.Contains("UninstallRegistryKey\"=", StringComparison.Ordinal), "value backup excludes other data");

Check(RegistryResidualPolicy.Evaluate(identity, snapshot with { Values = new Dictionary<string, string> { ["DisplayName"] = identity.Name } }) is null,
    "name-only match refused");
Check(RegistryResidualPolicy.Evaluate(identity, snapshot with { SubkeyCount = 1 }) == RegistryResidualKind.Value,
    "key with descendants refused");
Check(RegistryResidualPolicy.Evaluate(identity with { Publisher = "Microsoft" }, snapshot) is null,
    "Windows vendor refused");
Check(RegistryResidualPolicy.Evaluate(identity with { InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Owned") }, snapshot) is null,
    "profile data refused");
Check(RegistryResidualPolicy.Evaluate(identity, snapshot with { View = 7 }) is null, "unknown Registry view refused");
Check(RegistryResidualPolicy.Evaluate(identity, snapshot with { StringValuesOnly = false }) is null, "unbacked value type refused");
var x86 = identity with { RegistrationPath = @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{22222222-2222-2222-2222-222222222222}", Architecture = "32-bit" };
var x86Values = new Dictionary<string, string>(values) { ["UninstallRegistryKey"] = x86.RegistrationPath };
Check(RegistryResidualPolicy.Evaluate(x86, snapshot with { View = 32, Values = x86Values }) == RegistryResidualKind.Key,
    "explicit 32-bit identity supported");
Check(RegistryResidualPolicy.Evaluate(identity, snapshot with { Values = new Dictionary<string, string>(values) { ["InstallLocation"] = root + " Other" } }) is null,
    "different install root refused");
Check(RegistryResidualPolicy.Evaluate(identity, snapshot with { Values = new Dictionary<string, string>(values, StringComparer.Ordinal) { ["installlocation"] = root } }) is null,
    "case-folded duplicate value refused");
var changed = evidence with { Snapshot = snapshot with { Values = new Dictionary<string, string>(values) { ["DisplayName"] = "Changed" } } };
try { _ = RegistryResidualPolicy.BackupReg(changed); throw new Exception("Changed snapshot was accepted."); }
catch (InvalidOperationException) { }
Console.WriteLine("PASS: pure policy and .reg backup checks; no Registry API or deletion was used.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
}
