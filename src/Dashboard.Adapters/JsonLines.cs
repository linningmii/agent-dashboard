using System.Text.Json;

namespace Dashboard.Adapters;

public static class JsonLines
{
    public static string? Text(this JsonElement json, string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static JsonElement Child(this JsonElement json, string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value : default;
    public static DateTimeOffset? Time(this JsonElement json, string name) => DateTimeOffset.TryParse(json.Text(name), out var value) ? value : null;
    public static long? Number(this JsonElement json, string name) => json.Child(name).ValueKind == JsonValueKind.Number && json.Child(name).TryGetInt64(out var value) ? value : null;
    public static string Clip(string? value, int max = 2000) => string.IsNullOrEmpty(value) ? "" : value[..Math.Min(value.Length, max)];
    public static IEnumerable<JsonElement> Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            JsonElement? parsed = null;
            try { using var doc = JsonDocument.Parse(line); parsed = doc.RootElement.Clone(); } catch (JsonException) { }
            if (parsed is { } item) yield return item;
        }
    }
    public static string Message(JsonElement item)
    {
        var content = item.Child("content");
        if (content.ValueKind != JsonValueKind.Array) return item.Text("text") ?? "";
        return string.Join("\n", content.EnumerateArray().Where(part => part.Text("type") is "text" or "output_text" or "Text").Select(part => part.Text("text"))).Trim();
    }
}
