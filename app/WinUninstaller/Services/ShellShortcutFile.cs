using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WinUninstaller.Services;

/// <summary>Unicode, read-only shortcut parsing. Never invokes Resolve or a target.</summary>
internal static class ShellShortcutFile
{
    internal static readonly Guid ClassId = new("00021401-0000-0000-C000-000000000046");
    internal static string? ReadTarget(string path) => Read(path)?.Target;

    internal sealed record Info(string Target, string Arguments);

    internal static Info? Read(string path)
    {
        object? value = null;
        try
        {
            value = Activator.CreateInstance(Type.GetTypeFromCLSID(ClassId, true)!);
            ((IPersistFile)value!).Load(Path.GetFullPath(path), 0x20); // STGM_READ | SHARE_DENY_WRITE.
            var target = new StringBuilder(260);
            ((Link)value).GetPath(target, target.Capacity, IntPtr.Zero, 4); // Raw path, no resolution/network search.
            var text = Environment.ExpandEnvironmentVariables(target.ToString().Trim().Trim('"'));
            if (!(text.Length is > 0 and < 259 && Path.IsPathFullyQualified(text))) return null;
            var arguments = new StringBuilder(2048);
            ((Link)value).GetArguments(arguments, arguments.Capacity);
            return new(Path.GetFullPath(text), arguments.ToString());
        }
        catch { return null; }
        finally { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Link
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short value);
        void SetHotkey(short value);
        void GetShowCmd(out int value);
        void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
}
