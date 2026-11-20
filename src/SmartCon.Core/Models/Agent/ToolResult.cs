using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCon.Core.Models.Agent;

public sealed class ToolResult
{
    public bool Success { get; init; }

    public JsonElement? Data { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Pack manuals attached to this result (#400). A write tool that runs before
    /// its pack was loaded carries the manual here, so the model reads it before
    /// the apply call without an extra round trip.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ToolResultSkill> Skills { get; init; } = Array.Empty<ToolResultSkill>();

    /// <summary>Exposes <see cref="Skills"/> as <c>skills</c> only when non-empty.</summary>
    [JsonPropertyName("skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolResultSkill>? SkillsPayload =>
        Skills.Count == 0 ? null : Skills;

    public string? ErrorCode { get; init; }

    public int AffectedCount { get; init; }

    /// <summary>
    /// Opaque marker of the document state the result was produced on
    /// (DocumentVersion GUID + save number). The model compares it between calls
    /// to distinguish "the object does not exist" from "the model changed".
    /// </summary>
    public string? DocumentVersion { get; init; }

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
        IReadOnlyList<string>? repairs = null,
        int affectedCount = 0)
    {
        return new ToolResult
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            Warnings = warnings ?? Array.Empty<string>(),
            Data = data,
            Repairs = repairs ?? Array.Empty<string>(),
            AffectedCount = affectedCount
        };
    }

    public ToolResult WithRepairs(IReadOnlyList<string>? repairs)
    {
        if (repairs is null || repairs.Count == 0 || Repairs.Count > 0)
            return this;

        return Copy(repairs);
    }

    public ToolResult WithDocumentVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || DocumentVersion is not null)
            return this;

        return new ToolResult
        {
            Success = Success,
            Data = Data,
            Message = Message,
            Warnings = Warnings,
            Skills = Skills,
            ErrorCode = ErrorCode,
            AffectedCount = AffectedCount,
            DocumentVersion = version,
            Repairs = Repairs
        };
    }

    public ToolResult WithMessageSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix))
            return this;

        return new ToolResult
        {
            Success = Success,
            Data = Data,
            Message = (Message ?? string.Empty) + suffix,
            Warnings = Warnings,
            Skills = Skills,
            ErrorCode = ErrorCode,
            AffectedCount = AffectedCount,
            DocumentVersion = DocumentVersion,
            Repairs = Repairs
        };
    }

    public ToolResult WithWarnings(IReadOnlyList<string>? warnings)
    {
        if (warnings is null || warnings.Count == 0)
            return this;

        return Copy(
            Repairs,
            Warnings.Concat(warnings).ToList());
    }

    public ToolResult WithSkills(IReadOnlyList<ToolResultSkill>? skills)
    {
        if (skills is null || skills.Count == 0 || Skills.Count > 0)
            return this;

        return new ToolResult
        {
            Success = Success,
            Data = Data,
            Message = Message,
            Warnings = Warnings,
            Skills = skills,
            ErrorCode = ErrorCode,
            AffectedCount = AffectedCount,
            DocumentVersion = DocumentVersion,
            Repairs = Repairs
        };
    }

    private ToolResult Copy(
        IReadOnlyList<string>? repairs = null,
        IReadOnlyList<string>? warnings = null) => new()
    {
        Success = Success,
        Data = Data,
        Message = Message,
        Warnings = warnings ?? Warnings,
        Skills = Skills,
        ErrorCode = ErrorCode,
        AffectedCount = AffectedCount,
        DocumentVersion = DocumentVersion,
        Repairs = repairs ?? Repairs
    };
}
