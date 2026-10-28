using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUninstaller.Services;

/// <summary>
/// Waits for a launched uninstaller and any descendants observed while it is alive.
/// A timeout is uncertainty; this class never terminates a process.
/// </summary>
internal static class ProcessTreeCompletionTracker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public static async Task<bool> WaitForCompletionAsync(
        Process root,
        IProgress<string>? progress,
        TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        var observed = new Dictionary<int, Process>();
        try
        {
            var rootExit = root.WaitForExitAsync();
            var childWaitReported = false;

            while (!rootExit.IsCompleted)
            {
                RememberDescendants(root.Id, observed);
                if (watch.Elapsed >= timeout)
                    return false;

                await Task.WhenAny(rootExit, Task.Delay(PollInterval));
            }

            await rootExit;
            RememberDescendants(root.Id, observed);

            while (true)
            {
                RememberDescendants(root.Id, observed);
                var live = observed.Values.Where(IsAlive).ToArray();
                if (live.Length == 0)
                    return true;

                if (!childWaitReported)
                {
                    progress?.Report($"The uninstall launcher exited; waiting for {live.Length} related process(es) to finish...");
                    childWaitReported = true;
                }

                if (watch.Elapsed >= timeout)
                    return false;

                await Task.Delay(PollInterval);
            }
        }
        finally
        {
            foreach (var process in observed.Values) process.Dispose();
        }
    }

    private static void RememberDescendants(int rootId, Dictionary<int, Process> observed)
    {
        Dictionary<int, int> parents;
        try { parents = ReadProcessParents(); }
        catch { return; }

        var ancestors = new HashSet<int>(observed.Keys) { rootId };
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in parents)
            {
                if (!ancestors.Contains(pair.Value) || ancestors.Contains(pair.Key))
                    continue;

                ancestors.Add(pair.Key);
                changed = true;
                if (observed.ContainsKey(pair.Key))
                    continue;

                try { observed[pair.Key] = Process.GetProcessById(pair.Key); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        } while (changed);
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static Dictionary<int, int> ReadProcessParents()
    {
        const uint Th32csSnapProcess = 0x00000002;
        using var snapshot = new SafeSnapshot(CreateToolhelp32Snapshot(Th32csSnapProcess, 0));
        if (snapshot.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        var result = new Dictionary<int, int>();
        if (!Process32First(snapshot, ref entry))
            return result;

        do
        {
            result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        } while (Process32Next(snapshot, ref entry));

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExecutableFile;
    }

    private sealed class SafeSnapshot(IntPtr handle) : SafeHandle(handle, true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeSnapshot snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeSnapshot snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
