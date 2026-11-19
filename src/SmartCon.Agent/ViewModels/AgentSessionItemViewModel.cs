using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentSessionItemViewModel : ObservableObject
{
    private readonly Action<AgentSessionItemViewModel>? _requestDelete;
    private readonly Action<AgentSessionItemViewModel>? _confirmDelete;
    private readonly Action<AgentSessionItemViewModel>? _cancelDelete;

    public AgentSessionItemViewModel(
        Guid id,
        string title,
        DateTime updatedAt,
        Action<AgentSessionItemViewModel>? requestDelete = null,
        Action<AgentSessionItemViewModel>? confirmDelete = null,
        Action<AgentSessionItemViewModel>? cancelDelete = null)
    {
        Id = id;
        _title = title;
        UpdatedAt = updatedAt;
        _requestDelete = requestDelete;
        _confirmDelete = confirmDelete;
        _cancelDelete = cancelDelete;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string _title;

    /// <summary>Inline delete confirmation inside the session dropdown.</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;

    public DateTime UpdatedAt { get; set; }

    [RelayCommand]
    private void RequestDelete() => _requestDelete?.Invoke(this);

    [RelayCommand]
    private void ConfirmDelete() => _confirmDelete?.Invoke(this);

    [RelayCommand]
    private void CancelDelete() => _cancelDelete?.Invoke(this);
}