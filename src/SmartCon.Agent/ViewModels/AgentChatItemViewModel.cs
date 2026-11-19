using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.Agent.ViewModels;

public enum AgentChatItemKind
{
    User = 0,
    Assistant = 1,
    Tool = 2,
    Error = 3,
    Info = 4
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

    public bool HasDetails => !string.IsNullOrEmpty(Details);

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;

    public string MarkdownText => IsStreaming ? string.Empty : Text;

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(MarkdownText));

    public bool HasHeader => !string.IsNullOrEmpty(Header);

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    public bool IsUser => Kind == AgentChatItemKind.User;

    public bool IsAssistant => Kind == AgentChatItemKind.Assistant;

    public bool IsTool => Kind == AgentChatItemKind.Tool;

    public bool IsError => Kind == AgentChatItemKind.Error;

    public bool IsInfo => Kind == AgentChatItemKind.Info;

    public string Alignment => IsUser ? "Right" : "Left";
}