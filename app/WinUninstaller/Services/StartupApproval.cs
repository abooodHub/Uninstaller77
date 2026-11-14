using System.Buffers.Binary;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WinUninstaller.Services;

internal static class StartupApproval
{
    private const string ApprovalRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    internal sealed record State(string Status, string? ChangeToken);

    internal static State Read(string source, string name, string? identity, bool machine, bool canChange)
    {
        try
        {
            using var root = OpenHive(machine);
            using var key = root.OpenSubKey(ApprovalRoot + source);
            var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var status = Decode(raw);
            var token = !machine && canChange && identity is not null && IsWritableFormat(raw)
                ? Fingerprint(identity, raw) : null;
            return new State(status, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new State("Unknown", null);
        }
    }

    internal static void SetCurrentUser(string source, string name, string identity, string expectedToken, bool enabled)
    {
        if (source is not ("Run" or "StartupFolder")) throw new ArgumentOutOfRangeException(nameof(source));
        using var root = OpenHive(machine: false);
        using var key = root.CreateSubKey(ApprovalRoot + source);
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (!IsWritableFormat(raw) || !string.Equals(Fingerprint(identity, raw), expectedToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Startup approval changed.");
        if (Decode(raw) == (enabled ? "Enabled" : "Disabled")) return;

        var updated = Encode(enabled, DateTime.UtcNow);
        key.SetValue(name, updated, RegistryValueKind.Binary);
        if (key.GetValue(name) is not byte[] persisted || !persisted.AsSpan().SequenceEqual(updated))
            throw new IOException("Startup approval could not be verified.");
    }

    internal static string Decode(object? raw) => raw switch
    {
        null => "Enabled",
        byte[] { Length: 12 } bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes) switch
        {
            2 or 6 => "Enabled",
            3 or 7 => "Disabled",
            _ => "Unknown",
        },
        _ => "Unknown",
    };

    internal static byte[] Encode(bool enabled, DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), utcNow.ToFileTimeUtc());
        return bytes;
    }

    internal static bool IsWritableFormat(object? raw) => raw is null ||
        raw is byte[] { Length: 12 } bytes && BinaryPrimitives.ReadInt32LittleEndian(bytes) is 2 or 3;

    internal static string Fingerprint(string identity, object? raw) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(identity + "\0" + (raw is byte[] bytes ? Convert.ToHexString(bytes) : "absent"))));

    private static RegistryKey OpenHive(bool machine) => RegistryKey.OpenBaseKey(
        machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
}
