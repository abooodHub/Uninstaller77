namespace WinUninstaller.Models;

public enum ResidualScanState
{
    NotScanned, Scanning, CompletedNoFindings, CompletedWithFindings, Unsupported, Failed, Cancelled
}

public sealed record ResidualScanResult(ResidualScanState State, IReadOnlyList<ResidualItem> Items, string Error = "", int ScannedLocations = 0)
{
    // Stored in the existing TEXT column; legacy text remains readable without rewriting history.
    public string StorageStatus => "ResidualScan:" + State;
    public bool Completed => State is ResidualScanState.CompletedNoFindings or ResidualScanState.CompletedWithFindings;
    public string Message(string language) => Services.UiStrings.Get(language, "residualScan" + State);
}
