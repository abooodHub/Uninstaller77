using System.IO;
using System.Text.Json;

namespace WinUninstaller.Services;

public static class UiStrings
{
    private static readonly IReadOnlyDictionary<string, string> English = Load("en");
    private static readonly IReadOnlyDictionary<string, string> Arabic = Load("ar");

    public static string Get(string languageCode, string key)
    {
        var source = languageCode == "ar" ? Arabic : English;
        return source.TryGetValue(key, out var value) ? value : key;
    }

    public static bool IsDispatcherAccessError(Exception exception) =>
        exception.ToString().Contains("The calling thread cannot access this object", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> Load(string languageCode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", $"Strings.{languageCode}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
