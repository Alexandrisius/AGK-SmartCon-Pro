using System.Text;

namespace SmartCon.Core.Models.Agent;

public sealed class AgentProjectContext
{
    public bool HasDocument { get; init; } = true;

    public required string DocumentId { get; init; }

    public required string Title { get; init; }

    public string? PathName { get; init; }

    public required string RevitVersion { get; init; }

    public string? Units { get; init; }

    public bool IsWorkshared { get; init; }

    public bool IsModified { get; init; }

    public long? ActiveViewId { get; init; }

    public string? ActiveViewName { get; init; }

    public string? ActiveViewType { get; init; }

    public int LevelCount { get; init; }

    public int ViewCount { get; init; }

    public int SheetCount { get; init; }

    public IReadOnlyList<string> TopCategories { get; init; } = Array.Empty<string>();

    public string BuildPromptSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Current Revit document");
        if (!HasDocument)
        {
            sb.AppendLine("- No project document is open.");
            return sb.ToString();
        }

        sb.AppendLine($"- Title: {AgentTextSanitizer.Sanitize(Title)}");
        sb.AppendLine($"- DocumentId: {AgentTextSanitizer.Sanitize(DocumentId)}");
        if (!string.IsNullOrWhiteSpace(PathName))
            sb.AppendLine($"- Path: {AgentTextSanitizer.Sanitize(PathName)}");
        sb.AppendLine($"- Revit version: {AgentTextSanitizer.Sanitize(RevitVersion)}");
        if (!string.IsNullOrWhiteSpace(Units))
            sb.AppendLine($"- Units: {AgentTextSanitizer.Sanitize(Units)}");
        sb.AppendLine($"- Workshared: {(IsWorkshared ? "yes" : "no")}; modified: {(IsModified ? "yes" : "no")}");
        if (ActiveViewId.HasValue)
        {
            sb.AppendLine(
                $"- Active view: {AgentTextSanitizer.Sanitize(ActiveViewName)} " +
                $"(id {ActiveViewId.Value}, {AgentTextSanitizer.Sanitize(ActiveViewType)})");
        }
        sb.AppendLine($"- Levels: {LevelCount}; views: {ViewCount}; sheets: {SheetCount}");
        if (TopCategories.Count > 0)
        {
            sb.AppendLine(
                "- Top categories: " +
                string.Join(", ", TopCategories.Select(AgentTextSanitizer.Sanitize)));
        }
        return sb.ToString();
    }
}