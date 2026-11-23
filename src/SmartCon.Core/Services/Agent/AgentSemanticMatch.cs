using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Tuning of <see cref="AgentClassificationMatcher"/>: the confidence cutoff,
/// how many codes survive the family stage and extra selection context for the
/// decision model.
/// </summary>
public sealed record ClassifyOptions(
    double MinScore = 0.5,
    int CandidateDepth = 6,
    string? Instructions = null)
{
    public const double DefaultMinScore = 0.5;

    public const int DefaultCandidateDepth = 6;

    public const int MinCandidateDepth = 2;

    public const int MaxCandidateDepth = 15;
}

/// <summary>
/// One element type selected for classification, copied off the Revit thread:
/// names, the current Assembly Code and the non-empty text values that describe
/// the type (the category display name, family name and type name are composed
/// by the matcher, not carried here).
/// </summary>
public sealed record ClassifyTypeSnapshot(
    long ElementId,
    string Category,
    string FamilyName,
    long? FamilyId,
    string TypeName,
    string? CurrentCode,
    IReadOnlyList<string> Values);

/// <summary>One family of the selection with its composed matching texts.</summary>
public sealed class ClassifyFamilyGroup
{
    public required string Key { get; init; }

    public required string Category { get; init; }

    public required string FamilyName { get; init; }

    public long? FamilyId { get; init; }

    public string FamilyText { get; internal set; } = string.Empty;

    public IReadOnlyList<string> FamilyNameTokens { get; internal set; } = Array.Empty<string>();

    public IReadOnlyList<string> TypeQueries { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// Per type, aligned with <see cref="Types"/>: the values NOT shared by
    /// every type of the family - the raw material of the per-type
    /// differentiation (empty for single-type families).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> TypeDistinctValues { get; internal set; } =
        Array.Empty<IReadOnlyList<string>>();

    public IReadOnlyList<ClassifyTypeSnapshot> Types { get; internal set; } =
        Array.Empty<ClassifyTypeSnapshot>();
}

/// <summary>What happened to one type: its proposed code or why there is none.</summary>
public enum ClassifyTypeStatus
{
    Proposed = 0,
    LowConfidence = 1,
    NoCandidates = 2,
    AlreadySet = 3,

    /// <summary>The pick came back without probabilities or confidence - a guess.</summary>
    NoConfidence = 4
}

/// <summary>An alternative code of an uncertain type, with its score and path.</summary>
public sealed record ClassifyCodeCandidate(string Code, double Score, string Breadcrumb);

/// <summary>The classification outcome of one type.</summary>
public sealed record ClassifyTypeResult(
    ClassifyTypeSnapshot Type,
    ClassifyTypeStatus Status,
    string? Code,
    double Score,
    IReadOnlyList<ClassifyCodeCandidate> Candidates)
{
    /// <summary>True reason of a no_candidates / uncertain outcome, when known.</summary>
    public string? Reason { get; init; }
}

/// <summary>One (family, code) group of proposed types: the report row.</summary>
public sealed record ClassifyAssignmentGroup(
    long? FamilyId,
    string Family,
    string Category,
    string Code,
    string Breadcrumb,
    double Score,
    int TypeIdCount,
    IReadOnlyList<long> TypeIds,
    IReadOnlyList<string> TypeNames);

/// <summary>One ready set_parameters item of the proposed codes.</summary>
public sealed record ClassifySetParameterItem(long ElementId, string Parameter, string Value, string Scope);

/// <summary>The aggregate result of a classify_types run.</summary>
public sealed class ClassifyMatchResult
{
    public IReadOnlyList<ClassifyTypeResult> Types { get; init; } =
        Array.Empty<ClassifyTypeResult>();

    public IReadOnlyList<ClassifyAssignmentGroup> Assignments { get; init; } =
        Array.Empty<ClassifyAssignmentGroup>();

    /// <summary>set_parameters items of the proposed codes, packed into calls of at most 500.</summary>
    public IReadOnlyList<IReadOnlyList<ClassifySetParameterItem>> SetParameterBatches { get; init; } =
        Array.Empty<IReadOnlyList<ClassifySetParameterItem>>();

    /// <summary>null, or the decision-level error when nothing was answered at all.</summary>
    public string? Error { get; init; }

    public string? ErrorDetail { get; init; }

    public bool Incomplete { get; init; }

    /// <summary>Family (or family / type) names the decision layer did not answer.</summary>
    public IReadOnlyList<string> NotProcessed { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>True when the decision model answered at least one question.</summary>
    public bool DecisionUsed { get; init; }

    /// <summary>Decision calls the run made (all stages together).</summary>
    public int DecisionCalls { get; init; }

    /// <summary>Families the matching actually processed.</summary>
    public int FamiliesProcessed { get; init; }

    /// <summary>Types that already carry the proposed code, capped for the payload.</summary>
    public IReadOnlyList<ClassifyTypeResult> AlreadySetItems { get; init; } =
        Array.Empty<ClassifyTypeResult>();

    /// <summary>
    /// setParameters EXACTLY as set_parameters accepts it: <c>{items}</c> for one
    /// batch of at most 500 items, <c>{calls:[{items}…]}</c> above 500 (one call
    /// per entry - the tool has no batch argument) - no extra keys inside, so the
    /// argument passes the tool schema as is.
    /// </summary>
    public object? BuildSetParametersPayload()
    {
        if (SetParameterBatches.Count == 0)
            return null;

        if (SetParameterBatches.Count == 1)
            return new { items = SetParameterBatches[0].Select(Item).ToList() };

        // set_parameters takes at most 500 items per call and has no batch
        // argument: one entry per call.
        return new
        {
            calls = SetParameterBatches
                .Select(batch => new { items = batch.Select(Item).ToList() })
                .ToList()
        };

        static object Item(ClassifySetParameterItem item) => new
        {
            elementId = item.ElementId,
            parameter = item.Parameter,
            value = item.Value,
            scope = item.Scope
        };
    }
}
