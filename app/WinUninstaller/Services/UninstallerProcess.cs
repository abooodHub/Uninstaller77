using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinUninstaller.Services;

/// <summary>A suspended launch assigned to a Windows job before its first instruction.
/// The job has no termination flag: disposal never kills an external installer.</summary>
internal sealed class UninstallerProcess : IDisposable
{
    private readonly SafeFileHandle? _job;
    private readonly UninstallBroker.Session? _broker;
    private readonly BrowserHandoffContext? _handoffContext;
    internal int[] BrowserHandoffProcessIds { get; private set; } = [];
    internal bool IsRemovalBoundaryComplete => _broker?.CompletionVerified ?? (BrowserHandoffProcessIds.Length > 0 || IsTreeComplete);
    public Process Root { get; }
    public int ExecutionId => _broker?.ChildId ?? Root.Id;
    public bool HasReliableTree => _job is not null || _broker?.TrackingStarted == true;
    public int? ObservedExitCode
    {
        get
        {
            if (_broker is not null) return _broker.ExitCode;
            try { return Root.HasExited ? Root.ExitCode : null; }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return null; }
        }
    }
    private UninstallerProcess(Process root, SafeFileHandle? job, BrowserHandoffContext? context = null) { Root = root; _job = job; _handoffContext = context; }

    // Elevate our small native worker, not the third-party launcher. The worker
    // creates the same suspended/job-tracked launch after independently validating
    // the current registration. No descendant can run before job assignment.
    public static async Task<UninstallerProcess> StartRegisteredAsync(ProcessStartInfo info,
        string registryPath, bool quiet, CancellationToken cancellation = default, BrowserHandoffContext? context = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!info.UseShellExecute) return Start(info, context);
        var broker = await UninstallBroker.StartAsync(registryPath, quiet, info.FileName, info.Arguments, cancellation: cancellation);
        return new UninstallerProcess(broker.Process, broker);
    }

    private UninstallerProcess(Process root, UninstallBroker.Session broker) { Root = root; _broker = broker; }

    public static UninstallerProcess Start(ProcessStartInfo info, BrowserHandoffContext? context = null)
    {
        if (info.UseShellExecute)
        {
            var elevated = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
            // UAC brokered launches cannot be assigned before execution. Treat tree evidence
            // as incomplete even if a later snapshot happens to find some children.
            return new(elevated, (SafeFileHandle?)null);
        }
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder($"\"{info.FileName}\" {info.Arguments}");
        var flags = 4u | (info.CreateNoWindow ? 0x08000000u : 0u);
        if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out var pi))
        { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            if (!AssignProcessToJobObject(job, pi.Process))
            {
                // It is still suspended, so no destructive instructions have run.
                var error = Marshal.GetLastWin32Error();
                TerminateProcess(pi.Process, 1);
                throw new Win32Exception(error, "Cannot establish reliable process-tree tracking; uninstaller was not launched.");
            }
            var process = Process.GetProcessById((int)pi.ProcessId);
            // GetProcessById initially stores only the PID. Pin a managed process handle
            // while the child is still suspended, so a fast exit cannot discard its
            // exit-code evidence before WaitAsync/ExitCode reads it.
            try { _ = process.Handle; }
            catch
            {
                TerminateProcess(pi.Process, 1);
                process.Dispose();
                throw;
            }
            if (ResumeThread(pi.Thread) == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                TerminateProcess(pi.Process, 1);
                process.Dispose();
                throw new Win32Exception(error);
            }
            return new(process, job, context);
        }
        catch { job.Dispose(); throw; }
        finally { CloseHandle(pi.Thread); CloseHandle(pi.Process); }
    }

    public bool IsTreeComplete
    {
        get
        {
            if (_broker is not null) return _broker.TreeComplete;
            if (_job is null) return false;
            if (!QueryInformationJobObject(_job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return accounting.ActiveProcesses == 0;
        }
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, IProgress<string>? progress)
    {
        if (_broker is not null) return await _broker.WaitAsync(timeout);
        if (_job is null)
        {
            await ProcessTreeCompletionTracker.WaitForCompletionAsync(Root, progress, timeout);
            return false; // No guarantee that every elevated descendant was observed.
        }
        var clock = Stopwatch.StartNew();
        var reported = false;
        var nextHandoffCheck = TimeSpan.FromSeconds(2);
        while (!IsTreeComplete)
        {
            if (Root.HasExited && !reported)
            { progress?.Report("The uninstall launcher exited; waiting for related processes to finish..."); reported = true; }
            if (_handoffContext is not null && Root.HasExited && Root.ExitCode == 0 && clock.Elapsed >= nextHandoffCheck)
            {
                nextHandoffCheck = clock.Elapsed + TimeSpan.FromSeconds(2);
                var members = ReadJobProcessIds();
                if (members is { Length: > 0 } && !members.Contains(Root.Id) &&
                    await Task.Run(() => PostUninstallBrowserHandoff.VerifyAsync(members, _handoffContext)))
                {
                    var freshMembers = ReadJobProcessIds();
                    if (freshMembers is not null && members.Order().SequenceEqual(freshMembers.Order()))
                    {
                        BrowserHandoffProcessIds = members;
                        progress?.Report("External browser handoff was verified; the browser can remain open.");
                        return true; // Verified removal boundary, explicitly not an empty Windows job.
                    }
                }
            }
            if (clock.Elapsed >= timeout) return false;
            await Task.Delay(100);
        }
        await Root.WaitForExitAsync();
        return true;
    }

    private int[]? ReadJobProcessIds()
    {
        if (_job is null) return null;
        const int maximum = 128;
        var bytes = 8 + maximum * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!QueryJobProcesses(_job, 3, buffer, bytes, out _)) return null;
            var assigned = Marshal.ReadInt32(buffer); var count = Marshal.ReadInt32(buffer, 4);
            if (assigned != count || count is < 0 or > maximum) return null;
            return Enumerable.Range(0, count).Select(index => checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64())).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() { _broker?.Dispose(); Root.Dispose(); _job?.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo
    { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobAccounting info, int size, IntPtr returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryJobProcesses(SafeFileHandle job, int infoClass, IntPtr info, int size, out int returned);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
