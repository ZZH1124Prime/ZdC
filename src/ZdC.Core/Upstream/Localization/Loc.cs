using System.Globalization;
using System.Reflection;
using System.Text.Json;
namespace TrafikRuler.Localization;
// ZdC adapter for upstream diagnostic strings; no WPF/localization runtime dependency.
internal static class Loc
{
    private static readonly Dictionary<string, JsonElement> Strings = Load();
    private static Dictionary<string, JsonElement> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("upstream.en.json");
        return stream is null ? new() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream) ?? new();
    }
    public static string T(string key, params object[] args)
    {
        var text = Strings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : key;
        try { return string.Format(CultureInfo.InvariantCulture, text, args); }
        catch (FormatException) { return text; }
    }
}
