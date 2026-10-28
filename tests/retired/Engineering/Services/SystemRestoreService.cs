using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUninstaller.Services;

/// <summary>
/// Interacts with the native Windows System Restore subsystem.
/// Provides safe point creation prior to application removal.
/// </summary>
public static class SystemRestoreService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFO
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    private const int BEGIN_SYSTEM_CHANGE = 100;
    private const int END_SYSTEM_CHANGE = 101;
    private const int APPLICATION_UNINSTALL = 2;

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    private static extern int SRSetRestorePointW(ref RESTOREPOINTINFO pRestorePointInfo, ref STATEMGRSTATUS pSMgrStatus);

    public sealed record RestoreResult(bool Success, string Message, long SequenceNumber);

    public static async Task<RestoreResult> CreateRestorePointAsync(string description)
    {
        return await Task.Run(() =>
        {
            try
            {
                var info = new RESTOREPOINTINFO
                {
                    dwEventType = BEGIN_SYSTEM_CHANGE,
                    dwRestorePtType = APPLICATION_UNINSTALL,
                    llSequenceNumber = 0,
                    szDescription = $"WinUninstaller: {description}"
                };
                var status = new STATEMGRSTATUS();

                int res = SRSetRestorePointW(ref info, ref status);
                if (res == 0) // 0 = ERROR_SUCCESS
                {
                    return new RestoreResult(true, "System Restore point created successfully.", status.llSequenceNumber);
                }

                // Fallback to PowerShell Checkpoint-Computer if native API returned error
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description 'WinUninstaller: {description.Replace('\'', ' ')}' -RestorePointType APPLICATION_UNINSTALL -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null && proc.WaitForExit(15000) && proc.ExitCode == 0)
                {
                    return new RestoreResult(true, "System Restore point created via PowerShell.", 0);
                }

                return new RestoreResult(false, $"System Restore returned status code {res} (System Restore may be disabled on this drive).", 0);
            }
            catch (Exception ex)
            {
                return new RestoreResult(false, $"System Restore unavailable: {ex.Message}", 0);
            }
        });
    }
}
