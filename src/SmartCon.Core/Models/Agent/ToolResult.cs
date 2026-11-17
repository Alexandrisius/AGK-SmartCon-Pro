using System.Text.Json;

namespace SmartCon.Core.Models.Agent;

public sealed class ToolResult
{
    public bool Success { get; init; }

    public JsonElement? Data { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string? ErrorCode { get; init; }

    public int AffectedCount { get; init; }

    public static ToolResult Ok(
        JsonElement? data = null,
        string? message = null,
        int affectedCount = 0,
        IReadOnlyList<string>? warnings = null)
    {
        return new ToolResult
        {
            Success = true,
            Data = data,
            Message = message,
            AffectedCount = affectedCount,
            Warnings = warnings ?? Array.Empty<string>()
        };
    }

    public static ToolResult Fail(
        string errorCode,
        string message,
        IReadOnlyList<string>? warnings = null,
        JsonElement? data = null)
    {
        return new ToolResult
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            Warnings = warnings ?? Array.Empty<string>(),
            Data = data
        };
    }
}