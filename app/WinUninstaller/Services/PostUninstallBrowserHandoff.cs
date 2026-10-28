using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinUninstaller.Services;

internal sealed record BrowserHandoffContext(string Registration, string TargetName, string InstallLocation, string Uninstaller);

/// <summary>Recognizes only signed external browser processes after an observed
/// successful launcher exit and exact registration absence. Never kills or detaches a process.</summary>
internal static class PostUninstallBrowserHandoff
{
    internal sealed record ProcessDescription(int ProcessId, string ExecutablePath, string CommandLine);
    internal sealed record BrowserIdentity(string FileName, string OriginalName, string Product, string Signer);

    internal static bool AllowedDescription(BrowserIdentity identity, string path, string arguments, BrowserHandoffContext context)
    {
        var family = identity.FileName.ToLowerInvariant() switch
        {
            "msedge.exe" when identity.OriginalName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) && identity.Product.Contains("Edge", StringComparison.OrdinalIgnoreCase) && identity.Signer == "Microsoft Corporation" => "Edge",
            "chrome.exe" when identity.OriginalName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) && identity.Product.Contains("Chrome", StringComparison.OrdinalIgnoreCase) && identity.Signer is "Google LLC" or "Google Inc" or "Google Inc." => "Chrome",
            "brave.exe" when identity.OriginalName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) && identity.Product.Contains("Brave", StringComparison.OrdinalIgnoreCase) && identity.Signer is "Brave Software, Inc." or "Brave Software, Inc" => "Brave",
            "firefox.exe" when identity.OriginalName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase) && identity.Product.Contains("Firefox", StringComparison.OrdinalIgnoreCase) && identity.Signer == "Mozilla Corporation" => "Firefox",
            _ => ""
        };
        if (family.Length == 0 || context.TargetName.Contains(family, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(context.Uninstaller, StringComparison.OrdinalIgnoreCase) || Within(path, context.InstallLocation)) return false;
        if (Regex.IsMatch(arguments, @"(?:^|\s)--?(?:uninstall|install|setup|remove|browser-subprocess-path|renderer-cmd-prefix|utility-cmd-prefix|gpu-launcher)(?:[=\s]|$)", RegexOptions.IgnoreCase)) return false;
        return arguments.Contains("https://", StringComparison.OrdinalIgnoreCase) || arguments.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            arguments.Contains("about:", StringComparison.OrdinalIgnoreCase) || arguments.Contains("--headless", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(arguments, @"(?:^|\s)--type=(?:renderer|gpu-process|utility|crashpad-handler)(?:\s|$)", RegexOptions.IgnoreCase);
    }

    private static bool Within(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }

    internal static async Task<bool> VerifyAsync(int[] processIds, BrowserHandoffContext context)
    {
        if (processIds.Length is < 1 or > 128 || UninstallRegistration.Read(context.Registration).State != UninstallRegistration.Presence.Absent) return false;
        var pinned = new Dictionary<int, SafeProcessHandle>();
        try
        {
            foreach (var id in processIds)
            {
                var handle = OpenProcess(0x1000 | 0x100000, false, id); pinned.Add(id, handle);
                // Query-limited + synchronize is sufficient, including sandboxed renderers.
                if (handle.IsInvalid || WaitForSingleObject(handle, 0) != 258) return false;
            }
            var descriptions = await ReadDescriptionsAsync(processIds);
            if (descriptions.Count != processIds.Length) return false;
            foreach (var description in descriptions)
            {
                if (!pinned.TryGetValue(description.ProcessId, out var handle) || WaitForSingleObject(handle, 0) != 258 || string.IsNullOrWhiteSpace(description.CommandLine)) return false;
                var actualPath = new StringBuilder(32768); var length = actualPath.Capacity;
                if (!QueryFullProcessImageName(handle, 0, actualPath, ref length) || !string.Equals(actualPath.ToString(), description.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
                var identity = ReadTrustedIdentity(description.ExecutablePath);
                if (identity is null || !AllowedDescription(identity, description.ExecutablePath, description.CommandLine, context)) return false;
            }
            return UninstallRegistration.Read(context.Registration).State == UninstallRegistration.Presence.Absent;
        }
        catch { return false; }
        finally { foreach (var handle in pinned.Values) handle.Dispose(); }
    }

    private static async Task<List<ProcessDescription>> ReadDescriptionsAsync(int[] ids)
    {
        var filter = string.Join(" OR ", ids.Select(id => "ProcessId=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var script = "$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);@((Get-CimInstance Win32_Process -Filter '" + filter +
            "' -ErrorAction Stop)|Select-Object ProcessId,ExecutablePath,CommandLine)|ConvertTo-Json -Compress";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-Command"); info.ArgumentList.Add(script);
        using var query = Process.Start(info) ?? throw new IOException("Process description query did not start.");
        var output = query.StandardOutput.ReadToEndAsync(); var errors = query.StandardError.ReadToEndAsync();
        try { await query.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (TimeoutException) { try { query.Kill(); } catch { } return []; } // Only this owned read-only query.
        if (query.ExitCode != 0) return [];
        _ = await errors;
        var json = await output;
        if (json.Length > 262_144) return [];
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        return rows.Select(row => new ProcessDescription(row.GetProperty("ProcessId").GetInt32(), row.GetProperty("ExecutablePath").GetString() ?? "", row.GetProperty("CommandLine").GetString() ?? "")).ToList();
    }

    internal static BrowserIdentity? ReadTrustedIdentity(string path)
    {
        try
        {
            for (var directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!VerifySignature(path, file.SafeFileHandle.DangerousGetHandle())) return null;
            var version = FileVersionInfo.GetVersionInfo(path);
            using var signed = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signed);
            return new(Path.GetFileName(path), version.OriginalFilename ?? "", version.ProductName ?? "", certificate.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch { return null; }
    }

    private static bool VerifySignature(string path, IntPtr file)
    {
        var fileInfo = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path, File = file };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(fileInfo, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x1000 | 0x10 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try { return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0; }
        finally
        {
            data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustData
    { public uint Size; public IntPtr PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData, UrlReference; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
}
