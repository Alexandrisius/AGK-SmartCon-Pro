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
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Options);
}