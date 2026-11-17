using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentSessionItemViewModel : ObservableObject
{
    public AgentSessionItemViewModel(Guid id, string title, DateTime updatedAt)
    {
        Id = id;
        _title = title;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string _title;

    public DateTime UpdatedAt { get; set; }
}