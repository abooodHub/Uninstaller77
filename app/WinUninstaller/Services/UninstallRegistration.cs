using Microsoft.Win32;

namespace WinUninstaller.Services;

internal static class UninstallRegistration
{
    internal enum Presence { Unknown, Present, Absent }
    internal sealed record Observation(Presence State, string Error = "");

    // The same read-only identity boundary is used before launch, for completion
    // verification and by the review UI. Access errors are never called presence
    // or absence, and arbitrary Registry paths cannot become removal evidence.
    internal static Observation Read(string? path)
    {
        if (!UninstallBroker.IsRegistrationPath(path)) return new(Presence.Unknown, "Unsupported uninstall registration identity.");
        try
        {
            using var hive = RegistryKey.OpenBaseKey(path!.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
                ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
            using var key = hive.OpenSubKey(path[5..], writable: false);
            return new(key is null ? Presence.Absent : Presence.Present);
        }
        catch (Exception ex) { return new(Presence.Unknown, ex.GetType().Name + ": " + ex.Message); }
    }
}
