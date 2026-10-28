using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Unavailable classifications do not turn age/date coincidences into evidence.</summary>
public static class InventoryIntegrity
{
    // Update a cached catalog using a current read-only identity observation.
    // Queue/history records are separate and must never be rewritten as success.
    public static List<InstalledApp> ReconcileRegistration(IEnumerable<InstalledApp> catalog, InstalledApp observed, string state)
    {
        if (state != "Absent" || string.IsNullOrWhiteSpace(observed.Id) ||
            observed.Source != "MSIX" && string.IsNullOrWhiteSpace(observed.RegistryKeyPath)) return catalog.ToList();
        return catalog.Where(item => item.Id != observed.Id || item.Source != observed.Source ||
            !string.Equals(item.RegistryKeyPath, observed.RegistryKeyPath, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    public const bool UsageClassificationAvailable = false;
    public const bool BundleClassificationAvailable = false;
    public static List<InstalledApp> CurrentRegistrations(IEnumerable<InstalledApp> discoveries) => discoveries
        .Where(app => app.Source is "Registry" or "MSIX" or "Fixture")
        .ToList();
}
