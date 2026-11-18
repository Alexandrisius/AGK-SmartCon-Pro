namespace SmartCon.Core.Models.Agent;

public sealed class WorkingState
{
    public string? Goal { get; set; }

    public List<string> Constraints { get; set; } = new();

    public string? PlanId { get; set; }

    public List<string> CompletedSteps { get; set; } = new();

    public List<string> PendingSteps { get; set; } = new();

    public long? ActiveViewId { get; set; }

    public string? ActiveViewName { get; set; }

    public List<string> SelectedUniqueIds { get; set; } = new();

    public List<string> TouchedElementIds { get; set; } = new();

    public List<string> ChangeSetIds { get; set; } = new();

    public List<string> OpenQuestions { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? DocumentId { get; set; }

    public string? DocumentPath { get; set; }

    public string? RevitVersion { get; set; }
}
