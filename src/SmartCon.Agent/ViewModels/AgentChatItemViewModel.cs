using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.Agent.ViewModels;

public enum AgentChatItemKind
{
    User = 0,
    Assistant = 1,
    Tool = 2,
    Error = 3,
    Info = 4,
    WorkSummary = 5,
    Metrics = 6
}

public sealed partial class AgentChatItemViewModel : ObservableObject
{
    public AgentChatItemViewModel(AgentChatItemKind kind, string? toolCallId = null)
    {
        Kind = kind;
        ToolCallId = toolCallId;
    }

    public AgentChatItemKind Kind { get; }

    public string KindName => Kind.ToString();

    public string? ToolCallId { get; }

    public string? ToolName { get; init; }

    public IReadOnlyList<AgentAttachmentItemViewModel> Attachments { get; init; } =
        Array.Empty<AgentAttachmentItemViewModel>();

    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeader))]
    private string _header = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkdownText))]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isSuccess = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private string _details = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Streamed chain-of-thought of one assistant message. Shown in its own
    /// collapsible block above the answer and auto-collapsed when the answer or a
    /// tool call starts (H5.5).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning))]
    private string _reasoningText = string.Empty;

    [ObservableProperty]
    private string _reasoningHeader = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoningStatus))]
    private string _reasoningStatus = string.Empty;

    [ObservableProperty]
    private bool _isReasoningExpanded = true;

    [ObservableProperty]
    private bool _isReasoningStreaming;

    /// <summary>
    /// True for tool cards and intermediate assistant narration of the current
    /// turn. At TurnCompleted every work item except the final answer is hidden
    /// and one WorkSummary row toggles them back.
    /// </summary>
    [ObservableProperty]
    private bool _isWorkItem;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isWorkExpanded;

    /// <summary>
    /// Items hidden behind this WorkSummary row. The summary command flips their
    /// IsVisible in place, so the transcript order stays intact.
    /// </summary>
    public IReadOnlyList<AgentChatItemViewModel> WorkItems { get; init; } =
        Array.Empty<AgentChatItemViewModel>();

    public bool HasDetails => !string.IsNullOrEmpty(Details);

    public bool HasReasoning => !string.IsNullOrEmpty(ReasoningText);

    public bool HasReasoningStatus => !string.IsNullOrEmpty(ReasoningStatus);

    public string MarkdownText => IsStreaming ? string.Empty : Text;

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(MarkdownText));

    public bool HasHeader => !string.IsNullOrEmpty(Header);

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    public bool IsUser => Kind == AgentChatItemKind.User;

    public bool IsAssistant => Kind == AgentChatItemKind.Assistant;

    public bool IsTool => Kind == AgentChatItemKind.Tool;

    public bool IsError => Kind == AgentChatItemKind.Error;

    public bool IsInfo => Kind == AgentChatItemKind.Info;

    public bool IsWorkSummary => Kind == AgentChatItemKind.WorkSummary;

    public bool IsMetrics => Kind == AgentChatItemKind.Metrics;

    public string Alignment => IsUser ? "Right" : "Left";

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleReasoning() => IsReasoningExpanded = !IsReasoningExpanded;

    [RelayCommand]
    private void ToggleWork()
    {
        IsWorkExpanded = !IsWorkExpanded;
        foreach (var item in WorkItems)
            item.IsVisible = IsWorkExpanded;
    }
}