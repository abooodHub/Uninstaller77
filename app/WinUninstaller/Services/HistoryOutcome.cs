namespace WinUninstaller.Services;

public static class HistoryOutcome
{
    public static string Normalize(bool success, string? recorded)
    {
        if (success) return string.IsNullOrEmpty(recorded) || recorded == "Succeeded" ? "Succeeded" : "NeedsAttention";
        return recorded switch
        {
            "Failed" or "Cancelled" or "NotStarted" or "NotInstalled" or "NeedsAttention" or "FilesQuarantined" or "FilesRestored" or "FilesDeleted" or "FileDeletionIncomplete" => recorded,
            null or "" => "LegacyUnverified",
            _ => "NeedsAttention"
        };
    }
}
