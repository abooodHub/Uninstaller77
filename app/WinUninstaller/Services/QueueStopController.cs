namespace WinUninstaller.Services;

/// <summary>Stop notification is independent of disk writes and cannot lose a review race.</summary>
public sealed class QueueStopController
{
    private readonly object _gate = new();
    private TaskCompletionSource<UninstallService.UninstallResult?>? _review;
    private bool _stop;
    public bool StopRequested { get { lock (_gate) return _stop; } }
    public void RequestStop()
    {
        lock (_gate) { _stop = true; _review?.TrySetResult(null); }
    }
    public Task<UninstallService.UninstallResult?> WaitForReviewAsync()
    {
        lock (_gate)
        {
            if (_stop) return Task.FromResult<UninstallService.UninstallResult?>(null);
            _review = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _review.Task;
        }
    }
    public bool TryResolve(UninstallService.UninstallResult result)
    {
        lock (_gate) return !_stop && _review?.TrySetResult(result) == true;
    }
}
