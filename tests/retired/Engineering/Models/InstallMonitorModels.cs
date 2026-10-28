namespace WinUninstaller.Models;

public enum InstallMonitorState
{
    Idle, Preparing, CapturingBaseline, Monitoring, StopRequested, Stopping,
    CapturingFinalState, Comparing, Completed, Cancelled, Failed, Interrupted
}

public enum MonitorAssociation
{
    ConfirmedAssociation, LikelyAssociation, ObservedDuringSession, UnknownAssociation
}

public sealed record InstallMonitorEvent(
    string EventId, string SessionId, DateTimeOffset TimestampUtc, string Category,
    string ChangeType, string Target, string PreviousMetadata, string NewMetadata,
    MonitorAssociation Association, string Source, int? ProcessId = null, string? Diagnostic = null);

public sealed record InstallMonitorSession(
    string SessionId, string Name, string ScopePath, string InstallerPath,
    string InstallerPublisher, string InstallerSignatureStatus,
    string State, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc,
    int EventCount, int ErrorCount, bool CoverageComplete, string WarningsJson,
    string SummaryJson, double CpuSeconds, long MemoryDeltaBytes);
