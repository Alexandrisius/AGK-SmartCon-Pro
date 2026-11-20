namespace SmartCon.Core.Models.Agent;

/// <summary>
/// A pack operating manual attached to a tool result (#400). It is used when a
/// write tool runs before its pack was loaded: the manual arrives with the first
/// result, so the model reads it before the apply call instead of relying on a
/// prompt reminder.
/// </summary>
public sealed class ToolResultSkill
{
    public required string Pack { get; init; }

    public required string Markdown { get; init; }
}