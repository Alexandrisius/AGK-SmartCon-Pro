using System.Text.Encodings.Web;
using System.Text.Json;

namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// JSON contract shared by Core, the SQLite store and the Revit runner: camelCase,
/// case-insensitive reads, no indentation. Keeping it in one place prevents a
/// silent mismatch between the stored spec and the deserializer.
/// </summary>
public static class JobJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        // Same encoder as AgentJson: the default one escapes every non-ASCII
        // character, so family names and parameters reached the model as
        // \uXXXX inside job payloads and the stored spec/preview JSON.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json!, Options);
}