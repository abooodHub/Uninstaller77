using System.Globalization;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public static class CsvExportService
{
    public static string Cell(string? value)
    {
        var cell = value ?? "";
        var significant = cell.TrimStart(' ', '\t', '\r', '\n', '\uFEFF');
        if (cell.Length > 0 && (char.IsControl(cell[0]) || significant.Length > 0 && "=+-@".Contains(significant[0])))
            cell = "'" + cell;
        return "\"" + cell.Replace("\"", "\"\"") + "\"";
    }

    // The caller supplies the full filtered result set, not merely the virtualized viewport.
    public static List<string> FilteredResults(IEnumerable<InstalledApp> filtered) =>
        new[] { "Name,Publisher,Version,SizeKB,InstallDate,Source,Category" }.Concat(filtered.Select(a =>
            string.Join(',', new[] { Cell(a.Name), Cell(a.Publisher), Cell(a.Version),
                a.EstimatedSizeKB.ToString(CultureInfo.InvariantCulture), Cell(a.InstallDate), Cell(a.Source),
                Cell(a.IsBuiltInWindowsApp ? "Windows Built-in" : a.IsThirdPartyWindowsApp ? "Windows Third-Party" : "Standard App") }))).ToList();
}
