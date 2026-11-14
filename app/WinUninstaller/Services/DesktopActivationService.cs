using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace WinUninstaller.Services;

internal sealed record DesktopActivationRequest(string Operation, string? ShortcutPath = null);

/// <summary>Routes shell requests to the existing desktop process for the current Windows user.</summary>
internal sealed class DesktopActivationService : IDisposable
{
    internal const string PipeName = "Uninstaller77.Desktop.Activation.v1";
    private const int MaximumMessageLength = 32768;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<DesktopActivationRequest, Task> _handler;
    private readonly Task _listener;

    internal DesktopActivationService(Func<DesktopActivationRequest, Task> handler, string pipeName = PipeName)
    {
        _handler = handler;
        _listener = ListenAsync(pipeName, _shutdown.Token);
    }

    internal static async Task<bool> TryForwardAsync(DesktopActivationRequest request, string pipeName = PipeName,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(request);
        if (payload.Length > MaximumMessageLength) return false;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(450, cancellationToken).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                var acknowledgment = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                return string.Equals(acknowledgment, "OK", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                if (ex is OperationCanceledException) return false;
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }
        return false;
    }

    private async Task ListenAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null || line.Length > MaximumMessageLength)
                {
                    await writer.WriteLineAsync("INVALID").ConfigureAwait(false);
                    continue;
                }

                DesktopActivationRequest? request;
                try { request = JsonSerializer.Deserialize<DesktopActivationRequest>(line); }
                catch (JsonException) { request = null; }
                if (!IsValid(request))
                {
                    await writer.WriteLineAsync("INVALID").ConfigureAwait(false);
                    continue;
                }

                await _handler(request!).ConfigureAwait(false);
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static bool IsValid(DesktopActivationRequest? request) => request switch
    {
        { Operation: "activate", ShortcutPath: null } => true,
        { Operation: "shell-uninstall", ShortcutPath: { Length: > 0 and <= MaximumMessageLength } path }
            when Path.IsPathFullyQualified(path) => true,
        _ => false
    };

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _shutdown.Dispose();
    }
}
