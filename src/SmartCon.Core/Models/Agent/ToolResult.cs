using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCon.Core.Models.Agent;

public sealed class ToolResult
{
    public bool Success { get; init; }

    public JsonElement? Data { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string? ErrorCode { get; init; }

    public int AffectedCount { get; init; }

    /// <summary>
    /// Argument repairs applied by the executor before the tool ran (H1-09).
    /// The JSON payload exposes them as <c>_repairs</c> only when non-empty
    /// (through <see cref="RepairsPayload"/>).
    /// </summary>
    [JsonIgnore]
    [JsonPropertyName("_repairs")]
    public IReadOnlyList<string> Repairs { get; init; } = Array.Empty<string>();

    [JsonPropertyName("_repairs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RepairsPayload =>
        Repairs.Count == 0 ? null : Repairs;

    public static ToolResult Ok(
        JsonElement? data = null,
        string? message = null,
        int affectedCount = 0,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? repairs = null)
    {
        return new ToolResult
        {
            Success = true,
            Data = data,
            Message = message,
            AffectedCount = affectedCount,
            Warnings = warnings ?? Array.Empty<string>(),
            Repairs = repairs ?? Array.Empty<string>()
        };
    }

    public static ToolResult Fail(
        string errorCode,
        string message,
        IReadOnlyList<string>? warnings = null,
        JsonElement? data = null,
        IReadOnlyList<string>? repairs = null)
    {
        return new ToolResult
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            Warnings = warnings ?? Array.Empty<string>(),
            Data = data,
            Repairs = repairs ?? Array.Empty<string>()
        };
    }

    public ToolResult WithRepairs(IReadOnlyList<string>? repairs)
    {
        if (repairs is null || repairs.Count == 0 || Repairs.Count > 0)
            return this;

        return Copy(repairs);
    }

    public ToolResult WithWarnings(IReadOnlyList<string>? warnings)
    {
        if (warnings is null || warnings.Count == 0)
            return this;

        return Copy(
            Repairs,
            Warnings.Concat(warnings).ToList());
    }

    private ToolResult Copy(
        IReadOnlyList<string>? repairs = null,
        IReadOnlyList<string>? warnings = null) => new()
    {
        Success = Success,
        Data = Data,
        Message = Message,
        Warnings = warnings ?? Warnings,
        ErrorCode = ErrorCode,
        AffectedCount = AffectedCount,
        Repairs = repairs ?? Repairs
    };
}
