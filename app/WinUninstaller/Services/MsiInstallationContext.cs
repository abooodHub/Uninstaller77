using System.Runtime.InteropServices;
using System.Text;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

internal static class MsiInstallationContext
{
    private const uint UserManaged = 1;
    private const uint UserUnmanaged = 2;
    private const uint Machine = 4;
    private const uint UnknownProduct = 1605;

    internal static bool IsExclusiveUnmanagedUserProduct(InstalledApp app, string? productCode)
    {
        var key = app.RegistryKeyPath.Split('\\').LastOrDefault();
        if (app.RegistryHive is not ("HKCU" or "HKLM") || !Guid.TryParseExact(key, "B", out var registeredProduct) ||
            !Guid.TryParseExact(productCode, "B", out var commandProduct) || registeredProduct != commandProduct)
            return false;

        var code = commandProduct.ToString("B").ToUpperInvariant();
        var state = new StringBuilder(16);
        uint length = (uint)state.Capacity;
        // The display registration can be in HKLM even for an unmanaged user MSI.
        // State 5 means installed. A conflicting context or failed query retains the elevation path.
        return GetProductInfo(code, null, UserUnmanaged, "State", state, ref length) == 0 && state.ToString() == "5" &&
            IsAbsent(code, UserManaged) && IsAbsent(code, Machine) &&
            IsProductElevated(code, out var elevated) == 0 && !elevated;
    }

    private static bool IsAbsent(string code, uint context)
    {
        uint length = 0;
        return GetProductInfo(code, null, context, "State", null, ref length) == UnknownProduct;
    }

    [DllImport("msi.dll", EntryPoint = "MsiGetProductInfoExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetProductInfo(string productCode, string? userSid, uint context, string property,
        StringBuilder? value, ref uint length);

    [DllImport("msi.dll", EntryPoint = "MsiIsProductElevatedW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint IsProductElevated(string productCode, [MarshalAs(UnmanagedType.Bool)] out bool elevated);
}
