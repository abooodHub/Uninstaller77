using System.IO;
using System.Runtime.InteropServices;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Prepares and validates registered commands without launching a process.</summary>
internal static class RegisteredUninstallCommand
{
    internal sealed record LaunchCommand(string Executable, string Arguments, bool IsMsi);
    internal sealed class LaunchValidationException(string message, string detail, string failureCode = "None")
        : InvalidOperationException(message)
    {
        public string Detail { get; } = detail;
        public string FailureCode { get; } = failureCode;
    }

    // Shared by the desktop engine and its authenticated elevated worker. Both
    // validate a current registration; the worker never trusts a UI-supplied path.
    internal static LaunchCommand PrepareLaunch(InstalledApp app, bool preferQuiet)
    {
        var cmd = preferQuiet && app.HasQuietUninstall
            ? !string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.QuietUninstallString
                : BuildQuietCommand(app.UninstallString, app.InstallerType) ?? app.UninstallString
            : !string.IsNullOrWhiteSpace(app.UninstallString) ? app.UninstallString : app.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(cmd))
            throw new LaunchValidationException(
                "No uninstall command found.", $"No uninstall string found for '{app.Name}' in the Windows Registry.");
        if (cmd.Contains('\0'))
            throw new LaunchValidationException("Unsupported uninstall command.", "The registered command contains an invalid null character.");
        cmd = Environment.ExpandEnvironmentVariables(cmd);
        var (exe, args) = SplitCommand(cmd);
        if (!IsAllowedExecutable(exe))
            throw new LaunchValidationException(
                "Unsupported uninstall command.", "Only direct executable uninstallers are launched.");
        var isMsi = IsWindowsInstallerExecutable(exe);
        if (isMsi)
        {
            var product = MsiProductOperand(args);
            if (product is null) throw new LaunchValidationException(
                "Invalid MSI product code.", "The uninstall command has no valid MSI product code.", "InvalidMsiCommand");
            var keyName = app.RegistryKeyPath[(app.RegistryKeyPath.LastIndexOf('\\') + 1)..];
            if (Guid.TryParseExact(keyName, "B", out var keyProduct) && keyProduct != Guid.ParseExact(product, "B"))
                throw new LaunchValidationException("The MSI product code does not match this registration.",
                    "No uninstaller was launched because the registered command points to a different MSI product.", "MsiProductMismatch");
            return new(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/X {product} /norestart" + (preferQuiet ? " /qn" : ""), true);
        }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(exe) || !File.Exists(exe))
            throw new LaunchValidationException("Uninstaller executable was not found.", exe, "MissingExecutable");
        return new(exe, args, false);
    }

    internal static bool IsWindowsInstallerExecutable(string executable)
    {
        if (executable.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Path.IsPathFullyQualified(executable)) return false;
        return new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) }
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Any(directory => Path.GetFullPath(executable).Equals(Path.Combine(directory, "msiexec.exe"), StringComparison.OrdinalIgnoreCase));
    }

    // Bind identity to the /X or /I operand, never a GUID in a property or log path.
    internal static string? MsiProductOperand(string arguments)
    {
        if (arguments.Length > 30000 || arguments.Contains('\0')) return null;
        var pointer = CommandLineToArgv("msiexec.exe " + arguments, out var count);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            string Token(int index) => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? "";
            string? product = null;
            for (var index = 1; index < count; index++)
            {
                var token = Token(index);
                string? operand = null;
                if (token.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
                    operand = ++index < count ? Token(index) : "";
                else if (token.StartsWith("/x", StringComparison.OrdinalIgnoreCase) || token.StartsWith("/i", StringComparison.OrdinalIgnoreCase))
                    operand = token.Length == 2 ? ++index < count ? Token(index) : "" : token[2..];
                if (operand is null) continue;
                if (product is not null || !Guid.TryParseExact(operand, "B", out _)) return null;
                product = operand;
            }
            return product;
        }
        finally { FreeCommandLine(pointer); }
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string command, out int count);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr FreeCommandLine(IntPtr memory);

    internal static bool RequiresElevation(InstalledApp app, string exe) =>
        app.RegistryHive == "HKLM" || app.InstallerType == InstallType.Msi ||
        exe.Contains("Program Files", StringComparison.OrdinalIgnoreCase) ||
        exe.Contains("System32", StringComparison.OrdinalIgnoreCase);

    internal static bool RequiresElevation(InstalledApp app, LaunchCommand command) => command.IsMsi
        ? !MsiInstallationContext.IsExclusiveUnmanagedUserProduct(app, MsiProductOperand(command.Arguments))
        : RequiresElevation(app, command.Executable);

    internal static bool IsAllowedExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        var name = Path.GetFileName(executable.Trim().Trim('"'));
        if (name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return !new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    internal static string? BuildQuietCommand(string registeredCommand, InstallType installerType)
    {
        if (installerType is not (InstallType.Msi or InstallType.InnoSetup)) return null;
        var (executable, arguments) = SplitCommand(Environment.ExpandEnvironmentVariables(registeredCommand));
        if (IsWindowsInstallerExecutable(executable))
        {
            var product = MsiProductOperand(arguments);
            if (product is not null) return $"MsiExec.exe /X{product} /qn /norestart";
        }
        else if (installerType == InstallType.InnoSetup && Path.IsPathFullyQualified(executable)
            && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var registeredArguments = arguments.Length == 0 ? "" : arguments + " ";
            return $"\"{executable}\" {registeredArguments}/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        }

        return null;
    }

    internal static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) return (string.Empty, string.Empty);

        // Quoted executable: "C:\Path\To\file.exe" /arg1 /arg2
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0)
                return (cmd[1..end].Trim(), cmd[(end + 1)..].Trim());
        }

        // An unquoted .exe is a candidate only at the end of the path token.
        for (var start = 0; start < cmd.Length;)
        {
            var suffix = cmd.IndexOf(".exe", start, StringComparison.OrdinalIgnoreCase);
            if (suffix < 0) break;
            var end = suffix + 4;
            start = end;
            if (end < cmd.Length && cmd[end] is not (' ' or '\t')) continue;
            var candidateExe = cmd[..end];
            if (Path.IsPathFullyQualified(candidateExe) && !candidateExe.Contains('"'))
                return (candidateExe, cmd[end..].Trim());
            break;
        }

        // Space separation is safe only for a known executable name (for example msiexec.exe).
        var space = cmd.AsSpan().IndexOfAny(' ', '\t');
        if (space > 0)
        {
            var token = cmd[..space].Trim();
            if (token.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                return (token, cmd[(space + 1)..].Trim());
            return (string.Empty, string.Empty);
        }

        return (cmd, string.Empty);
    }
}
