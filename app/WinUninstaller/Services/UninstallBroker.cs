using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace WinUninstaller.Services;

/// <summary>One UAC-approved launch, authenticated by the actual pipe peer PIDs.
/// No command comes from CLI arguments or a writable receipt file. The elevated
/// worker rereads the registration and compares the exact prepared command before
/// launch. A missing/failed receipt is uncertainty, never successful removal.</summary>
internal static class UninstallBroker
{
    internal const string Switch = "--uninstall-broker";
    private const string Prefix = "Uninstaller77.Uninstall.";
    private const int MaxFrame = 32768;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal sealed record Request(string RegistryPath, bool Quiet, string Executable, string Arguments);
    internal sealed record Message(string State, int? ProcessId = null, int? ExitCode = null, string? Error = null,
        string CompletionKind = "FullTree", int[]? BrowserProcessIds = null);

    internal static async Task<Session> StartAsync(string registryPath, bool quiet, string executable, string arguments,
        bool elevate = true, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var requestBytes = EncodeMessage(new Request(registryPath, quiet, executable, arguments));
        var pipeName = Prefix + Guid.NewGuid().ToString("N");
        var pipe = CreatePipe(pipeName);
        Process? process = null;
        var sent = false;
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = elevate, Verb = elevate ? "runas" : "", WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = !elevate };
            info.ArgumentList.Add(Switch); info.ArgumentList.Add(pipeName); info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var connection = pipe.WaitForConnectionAsync();
            cancellation.ThrowIfCancellationRequested();
            process = await Task.Run(() => Process.Start(info)) ?? throw new InvalidOperationException("The uninstall worker did not start.");
            await connection.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != process.Id)
                throw new InvalidOperationException("Uninstall worker identity mismatch; no command was sent.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ready = await ReadAsync<Message>(pipe, deadline.Token);
            if (ready.State != "Ready") throw new InvalidOperationException("The uninstall worker was not ready.");
            // The request is the commit boundary. Cancellation before it sends
            // no uninstall identity/command; after it we retain completion tracking.
            cancellation.ThrowIfCancellationRequested();
            sent = true;
            await WriteBytesAsync(pipe, requestBytes, deadline.Token);
            var session = new Session(process, pipe);
            try
            {
                var started = await ReadAsync<Message>(pipe, deadline.Token);
                if (started.State == "Rejected")
                    throw new BrokerRejectedException(started.Error ?? "Registration validation failed.");
                if (started.State != "Started" || started.ProcessId is not > 0 || started.ExitCode is not null)
                    throw new InvalidDataException("Invalid uninstall worker start evidence.");
                session.ChildId = started.ProcessId;
                session.TrackingStarted = true;
                session.Listen();
            }
            catch (BrokerRejectedException) { throw; }
            catch (Exception ex)
            {
                // The request may already have run. Keep the execution barrier.
                session.Fail(ex.Message);
            }
            return session;
        }
        catch (Exception ex) when (sent && ex is not BrokerRejectedException && process is not null)
        {
            var uncertain = new Session(process, pipe); uncertain.Fail(ex.Message); return uncertain;
        }
        catch { pipe.Dispose(); process?.Dispose(); throw; }
    }

    internal sealed class BrokerRejectedException(string message) : InvalidOperationException(message);

    internal sealed class Session(Process process, NamedPipeServerStream pipe) : IDisposable
    {
        public Process Process { get; } = process;
        public int? ChildId { get; internal set; }
        public bool TrackingStarted { get; internal set; }
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int? _exitCode;
        public int? ExitCode => _completion.Task.IsCompletedSuccessfully && _completion.Task.Result ? _exitCode : null;
        public bool CompletionVerified => _completion.Task.IsCompletedSuccessfully && _completion.Task.Result;
        public string CompletionKind { get; private set; } = "FullTree";
        public int[] BrowserProcessIds { get; private set; } = [];
        public bool TreeComplete => CompletionVerified && CompletionKind == "FullTree";
        public string Error { get; private set; } = "";
        internal void Fail(string error) { Error = error; _completion.TrySetResult(false); }
        internal void Listen() => _ = ReceiveAsync();
        private async Task ReceiveAsync()
        {
            try
            {
                var completed = await ReadAsync<Message>(pipe, CancellationToken.None);
                if (completed.State != "Completed" || completed.ProcessId != ChildId || completed.ExitCode is null)
                    throw new InvalidDataException(completed.Error ?? "No authenticated job completion was received.");
                if (completed.CompletionKind is not ("FullTree" or "ExternalBrowser") ||
                    completed.CompletionKind == "ExternalBrowser" && (completed.ExitCode != 0 || completed.BrowserProcessIds is not { Length: > 0 and <= 128 } ids ||
                        ids.Any(id => id <= 0 || id == ChildId || id == Process.Id) || ids.Distinct().Count() != ids.Length) ||
                    completed.CompletionKind == "FullTree" && completed.BrowserProcessIds is { Length: > 0 })
                    throw new InvalidDataException("Invalid browser handoff completion evidence.");
                _exitCode = completed.ExitCode;
                CompletionKind = completed.CompletionKind; BrowserProcessIds = completed.BrowserProcessIds ?? [];
                _completion.TrySetResult(true);
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        public async Task<bool> WaitAsync(TimeSpan timeout)
        {
            try { return await _completion.Task.WaitAsync(timeout); }
            catch (TimeoutException) { return false; }
        }
        public void Dispose() => pipe.Dispose(); // No kill flag and no process termination.
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != Switch || !IsPipeName(args[1]) || !int.TryParse(args[2], out var parentId) || parentId <= 0)
            return 2;
        // Pin the parent handle before connecting, and require the same apphost.
        using var parent = Process.GetProcessById(parentId);
        if (!SameExecutable(parent)) return 2;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parentId || parent.HasExited)
            return 2;
        await WriteAsync(pipe, new Message("Ready"), deadline.Token);
        var request = await ReadAsync<Request>(pipe, deadline.Token);
        ProcessStartInfo launch;
        try { launch = ValidateRequest(request); }
        catch (Exception ex) { await WriteAsync(pipe, new Message("Rejected", Error: ex.Message), deadline.Token); return 2; }

        // Once a child may have run, no communication error permits another launch.
        // Keep the job alive and wait for every child even if the UI disconnects.
        UninstallerProcess execution;
        try
        {
            var identity = RegistryDiscoveryService.ReadExactRegistration(request.RegistryPath) ?? throw new InvalidDataException("Registration disappeared before launch.");
            execution = UninstallerProcess.Start(launch, new(request.RegistryPath, identity.Name, identity.InstallLocation, launch.FileName));
        }
        catch (Exception ex) { await WriteAsync(pipe, new Message("Rejected", Error: ex.Message), deadline.Token); return 2; }
        using (execution)
        {
            await TryWriteAsync(pipe, new Message("Started", execution.Root.Id));
            while (!await execution.WaitAsync(TimeSpan.FromMinutes(1), null)) { }
            await TryWriteAsync(pipe, new Message("Completed", execution.Root.Id, execution.Root.ExitCode,
                CompletionKind: execution.BrowserHandoffProcessIds.Length > 0 ? "ExternalBrowser" : "FullTree",
                BrowserProcessIds: execution.BrowserHandoffProcessIds.Length > 0 ? execution.BrowserHandoffProcessIds : null));
            return 0;
        }
    }

    internal static ProcessStartInfo ValidateRequest(Request request)
    {
        if (!IsRegistrationPath(request.RegistryPath) || request.Executable.Length > 4096 || request.Arguments.Length > 16384)
            throw new InvalidDataException("Unsupported uninstall registration identity.");
        var current = RegistryDiscoveryService.ReadExactRegistration(request.RegistryPath);
        if (current is null) throw new InvalidDataException("The exact uninstall registration is no longer available.");
        var prepared = UninstallService.PrepareLaunch(current, request.Quiet);
        if (prepared.IsMsi || !prepared.Executable.Equals(request.Executable, StringComparison.OrdinalIgnoreCase) ||
            prepared.Arguments != request.Arguments)
            throw new InvalidDataException("The registered uninstall command changed after confirmation.");
        return new ProcessStartInfo(prepared.Executable, prepared.Arguments)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(prepared.Executable)! };
    }

    internal static bool IsRegistrationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096) return false;
        return new[] { "HKCU", "HKLM" }.Any(hive => new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall\",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" }.Any(root =>
            path.StartsWith(hive + "\\" + root, StringComparison.OrdinalIgnoreCase) &&
            path[(hive.Length + 1 + root.Length)..] is { Length: > 0 } name && !name.Contains('\\')));
    }

    internal static bool IsPipeName(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[Prefix.Length..], "N", out _);

    private static bool SameExecutable(Process other)
    {
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        return QueryFullProcessImageName(other.SafeHandle, 0, path, ref length) &&
            string.Equals(path.ToString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("User identity unavailable.");
        // Explicit SID ACL allows the same user's elevated token. Reject remote
        // clients, prevent second instances, and authenticate both PIDs as well.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{sid})", 1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000u | 0x00080000u, 8, 1, 4096, 4096, 0, ref security);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        await WriteBytesAsync(stream, EncodeMessage(message), token);
    }

    internal static byte[] EncodeMessage<T>(T message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length is <= 0 or > MaxFrame) throw new InvalidDataException("Invalid worker message size.");
        return bytes;
    }

    private static async Task WriteBytesAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > MaxFrame) throw new InvalidDataException("Invalid worker message size.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            doc.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != doc.RootElement.EnumerateObject().Count())
            throw new InvalidDataException("Invalid worker message fields.");
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty worker message.");
    }

    private static async Task TryWriteAsync(Stream stream, Message message)
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await WriteAsync(stream, message, timeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, IntPtr size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
