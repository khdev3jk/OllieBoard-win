using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllieBoard;

/// Small persistent key-value store (UserDefaults on macOS), kept in %APPDATA%\OllieBoard\settings.json.
static class Settings
{
    public static readonly string SupportDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OllieBoard");

    private static readonly string FilePath = Path.Combine(SupportDir, "settings.json");
    private static JsonObject? values;

    private static JsonObject Values
    {
        get
        {
            if (values != null) return values;
            try { values = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject; } catch { }
            return values ??= new JsonObject();
        }
    }

    private static void Set(string key, JsonNode? value)
    {
        Values[key] = value;
        try
        {
            Directory.CreateDirectory(SupportDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, Values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { }
    }

    public static bool Has(string key) => Values.ContainsKey(key);

    public static string? GetString(string key) =>
        Values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static void SetString(string key, string value) => Set(key, JsonValue.Create(value));

    public static bool GetBool(string key, bool fallback) =>
        Values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public static void SetBool(string key, bool value) => Set(key, JsonValue.Create(value));

    public static List<string>? GetStringList(string key) =>
        Values[key] is JsonArray a ? a.Select(n => n?.GetValue<string>()).OfType<string>().ToList() : null;

    public static void SetStringList(string key, IEnumerable<string> list) =>
        Set(key, new JsonArray(list.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
}
