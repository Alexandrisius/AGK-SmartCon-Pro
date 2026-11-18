namespace SmartCon.Core.Models.Agent.Eval;

/// <summary>What an eval task expects from the agent.</summary>
public enum AgentEvalTaskKind
{
    /// <summary>Read-only task: the model must call the expected read tools and answer.</summary>
    Read = 0,

    /// <summary>
    /// The user asks for a change, but the harness forbids applying it: the model
    /// must call the write tool with dryRun=true and never with dryRun=false.
    /// </summary>
    WriteDryRun = 1,

    /// <summary>
    /// Two-phase task: dry run first, then the harness approves the apply, then
    /// the model's own change set is reverted with undo_last and the model state
    /// is verified.
    /// </summary>
    WriteApplyUndo = 2
}

/// <summary>Task-specific behavioural check implemented by the runner.</summary>
public enum AgentEvalCheck
{
    None = 0,

    /// <summary>X1: list_levels.total must equal get_project_overview.levelCount.</summary>
    LevelsCountConsistent = 1,

    /// <summary>X2: query_elements by ELEM_TYPE_PARAM name must find the seeded wall.</summary>
    TypeNameContains = 2,

    /// <summary>X3: get_view_settings without fields must return the compact summary.</summary>
    CompactViewSettings = 3,

    /// <summary>A1: "make like the reference view" copies settings and covers the rename.</summary>
    ReferenceViewPlan = 4,

    /// <summary>W15: unsupported request — no write calls, honest explanation.</summary>
    HonestUnsupported = 5
}

/// <summary>
/// One reproducible Russian task for the agent eval harness. Prompts may use
/// placeholders ({refViewId}, {targetAId}, {levelId}, {templateId}, {wallId},
/// {typeName}) that the runner substitutes with the seeded model ids.
/// </summary>
public sealed class AgentEvalTask
{
    public required string Id { get; init; }

    public required string Prompt { get; init; }

    public AgentEvalTaskKind Kind { get; init; }

    /// <summary>Every tool in this list must be called at least once.</summary>
    public IReadOnlyList<string> ExpectedTools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Acceptable substitutes: if the model used one of these instead of the last
    /// missing expected tool, the tool-selection check passes (e.g. set_parameters
    /// instead of remap_parameter_values for a three-element rename).
    /// </summary>
    public IReadOnlyList<string> AlternativeTools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Error codes that count as an honest, environment-limited outcome (for
    /// example no_active_view in the UI-less test host).
    /// </summary>
    public IReadOnlyList<string> AllowErrorCodes { get; init; } = Array.Empty<string>();

    /// <summary>Top-level data keys that must be present in a successful tool result.</summary>
    public IReadOnlyList<string> ExpectedDataKeys { get; init; } = Array.Empty<string>();

    /// <summary>Exact expected dry-run changed count (null = not checked).</summary>
    public int? ExpectedChanged { get; init; }

    /// <summary>Minimum expected dry-run changed count.</summary>
    public int? MinChanged { get; init; }

    /// <summary>Second user turn for WriteApplyUndo tasks.</summary>
    public string? ApplyPrompt { get; init; }

    public AgentEvalCheck Check { get; init; } = AgentEvalCheck.None;

    public string? Notes { get; init; }
}
