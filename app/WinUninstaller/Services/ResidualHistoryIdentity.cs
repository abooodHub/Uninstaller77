using System.Text.Json;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

// Only identities captured before uninstall may be used to rescan history.
internal sealed record ResidualHistoryIdentity(string AppId, string Name, string Publisher, string Version,
    string RegistryKeyPath, string InstallLocation, string PackageFamilyName, string Source, InstallType InstallerType, long? RegistrationLastWrite = null)
{
    internal static string Capture(InstalledApp app) => JsonSerializer.Serialize(new ResidualHistoryIdentity(
        app.Id, app.Name, app.Publisher, app.Version, app.RegistryKeyPath, app.InstallLocation,
        app.PackageFamilyName, app.Source, app.InstallerType, app.RegistrationLastWrite));

    internal static bool TryRestore(UninstallRecord record, out InstalledApp? app)
    {
        app = null;
        if (!record.Success || record.Outcome != "Succeeded" || record.ResidualIdentity.Length is 0 or > 16_384)
            return false;
        try
        {
            var identity = JsonSerializer.Deserialize<ResidualHistoryIdentity>(record.ResidualIdentity);
            if (identity is null || identity.AppId != record.AppId || identity.Name != record.AppName ||
                string.IsNullOrWhiteSpace(identity.Name) || identity.Name.Length > 512 ||
                identity.Publisher is null || identity.Version is null || identity.InstallLocation is null ||
                identity.PackageFamilyName is null || identity.Source is null ||
                identity.InstallLocation.Length > 4_096 || identity.PackageFamilyName.Length > 512)
                return false;
            var isMsix = identity.Source == "MSIX" && identity.InstallerType == InstallType.Msix;
            if (isMsix ? string.IsNullOrWhiteSpace(identity.PackageFamilyName) || !MsixDeploymentService.ValidIdentity(identity.AppId)
                : !UninstallBroker.IsRegistrationPath(identity.RegistryKeyPath) ||
                  UninstallRegistration.Read(identity.RegistryKeyPath).State != UninstallRegistration.Presence.Absent)
                return false;
            app = new InstalledApp
            {
                Id = identity.AppId, Name = identity.Name, Publisher = identity.Publisher,
                Version = identity.Version, RegistryKeyPath = identity.RegistryKeyPath,
                InstallLocation = identity.InstallLocation, PackageFamilyName = identity.PackageFamilyName,
                Source = identity.Source, InstallerType = identity.InstallerType,
                RegistrationLastWrite = identity.RegistrationLastWrite,
                CachedIconPath = record.CachedIconPath
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
        { return false; }
    }
}
