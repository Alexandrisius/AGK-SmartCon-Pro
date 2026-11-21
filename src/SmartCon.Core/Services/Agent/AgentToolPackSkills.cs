namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Pack skills (#400): short markdown operating manuals returned by
/// <c>load_tool_pack</c> together with a pack. A skill is the manual of one
/// pack — the map of its tools, the glossary of the arguments in Revit UI
/// terms, the Revit behaviour behind the API calls, the invariants a write must
/// respect and the recipes that worked on real models. Bodies live in partial
/// files grouped by domain; every populated pack has one (unit test
/// <c>AgentToolPackSkillsTests</c> is the gate).
/// </summary>
public static partial class AgentToolPackSkills
{
    private static readonly Dictionary<string, string> Skills = BuildSkills();

    /// <summary>Skill markdown of a pack, or null when the pack has no skill.</summary>
    public static string? TryGet(string pack) =>
        !string.IsNullOrEmpty(pack) && Skills.TryGetValue(pack, out var skill) ? skill : null;

    public static bool TryGet(string pack, out string? skill)
    {
        skill = TryGet(pack);
        return skill is not null;
    }

    /// <summary>Packs that carry a skill, in catalog order.</summary>
    public static IReadOnlyList<string> PacksWithSkill =>
        AgentToolPacks.All
            .Select(pack => pack.Name)
            .Where(Skills.ContainsKey)
            .ToArray();

    private static Dictionary<string, string> BuildSkills()
    {
        var skills = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(skills, ViewsSkills());
        Add(skills, ModelSkills());
        Add(skills, MepSkills());
        Add(skills, PlansSkills());
        Add(skills, FamiliesSkills());
        Add(skills, SchedulesSkills());
        Add(skills, SheetsSkills());
        Add(skills, JobsSkills());
        return skills;
    }

    private static void Add(
        Dictionary<string, string> target,
        IEnumerable<(string Pack, string Markdown)> source)
    {
        foreach (var (pack, markdown) in source)
            target.Add(pack, markdown);
    }
}