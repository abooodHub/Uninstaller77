using WinUninstaller.Models;

namespace WinUninstaller.Services;

public sealed record QueueReviewObservation(string State, string Evidence);

public static class QueueReviewVerifier
{
    // Observed absence is not proof that the historical uninstall succeeded.
    public static async Task<QueueReviewObservation> ObserveAsync(InstalledApp app)
    {
        if (app.Source == "MSIX" || app.InstallerType == InstallType.Msix)
        {
            var query = await MsixDeploymentService.QueryAsync(app.Id);
            return new(query.Verification == VerificationStatus.Unknown ? "Unknown" :
                query.Verification == VerificationStatus.StillPresent ? "Present" : "Absent", query.FailureCode + " " + query.Error);
        }
        var observed = UninstallRegistration.Read(app.RegistryKeyPath);
        return new(observed.State.ToString(), string.IsNullOrWhiteSpace(observed.Error)
            ? "Read-only Registry observation; historical completion remains unresolved." : observed.Error);
    }
}
