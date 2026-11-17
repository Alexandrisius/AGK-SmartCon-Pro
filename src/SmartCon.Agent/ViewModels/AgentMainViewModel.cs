using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.UI;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.UI;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel : ObservableObject, IDisposable
{
    private const int MaxStoredToolResultPreview = 400;

    private readonly IAgentConversationStore _conversationStore;
    private readonly AgentOrchestrator _orchestrator;
    private readonly IAgentProjectContextProvider _projectContextProvider;
    private readonly IAgentLlmClient _llmClient;
    private readonly AgentLlmSettings _settings;
    private readonly IAgentFilePicker _filePicker;
    private readonly IAgentClipboardService _clipboardService;
    private readonly AgentConfirmationService _confirmationService;
    private readonly IActiveDocumentChangeNotifier? _documentChangeNotifier;
    private readonly IDispatcher _dispatcher;
    private bool _hasApiKey;
    private readonly Dictionary<string, AgentChatItemViewModel> _toolCards =
        new(StringComparer.Ordinal);

    private AgentProjectContext? _currentContext;
    private CancellationTokenSource? _turnCancellation;
    private AgentChatItemViewModel? _currentAssistant;
    private bool _isRefreshing;
    private bool _refreshAfterTurn;
    private bool _disposed;

    public AgentMainViewModel(
        IAgentConversationStore conversationStore,
        AgentOrchestrator orchestrator,
        IAgentProjectContextProvider projectContextProvider,
        IAgentLlmClient llmClient,
        AgentLlmSettings settings,
        IAgentFilePicker filePicker,
        IAgentClipboardService clipboardService,
        AgentConfirmationService confirmationService,
        IDispatcher dispatcher,
        IActiveDocumentChangeNotifier? documentChangeNotifier = null)
    {
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _projectContextProvider = projectContextProvider
            ?? throw new ArgumentNullException(nameof(projectContextProvider));
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _confirmationService = confirmationService
            ?? throw new ArgumentNullException(nameof(confirmationService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _documentChangeNotifier = documentChangeNotifier;
        _hasApiKey = settings.HasApiKey;

        AttachConfirmationService(_confirmationService);
        Messages.CollectionChanged += OnMessagesChanged;
        Attachments.CollectionChanged += OnAttachmentsChanged;
        if (_documentChangeNotifier is not null)
            _documentChangeNotifier.ActiveDocumentChanged += OnActiveDocumentChanged;

        UpdateBanner();
        if (!_hasApiKey)
        {
            StatusText = S("Agent_Status_NoApiKey", "Ключ LLM не настроен");
            HasError = true;
        }

        FireAndForget(InitializeAsync(), nameof(InitializeAsync));
    }

    public ObservableCollection<AgentChatItemViewModel> Messages { get; } = new();

    public ObservableCollection<AgentSessionItemViewModel> Sessions { get; } = new();

    public ObservableCollection<AgentAttachmentItemViewModel> Attachments { get; } = new();

    public bool HasApiKey => _hasApiKey;

    public bool IsChatEmpty => Messages.Count == 0;

    public bool HasAttachments => Attachments.Count > 0;

    public string ProviderLabel => _llmClient.ProviderName + " · " + _llmClient.Model;

    private string NewChatTitle => S("Agent_NewChatTitle", "Новый чат");

    [ObservableProperty]
    private AgentSessionItemViewModel? _selectedSession;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusyVisible))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isTyping;

    [ObservableProperty]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private bool _hasDocument;

    [ObservableProperty]
    private bool _showBanner;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    [ObservableProperty]
    private string _documentTitle = string.Empty;

    [ObservableProperty]
    private string _tokenSummary = "—";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    public bool IsBusyVisible => IsBusy;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_documentChangeNotifier is not null)
            _documentChangeNotifier.ActiveDocumentChanged -= OnActiveDocumentChanged;

        DetachConfirmationService(_confirmationService);
        Messages.CollectionChanged -= OnMessagesChanged;
        Attachments.CollectionChanged -= OnAttachmentsChanged;
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        _turnCancellation = null;
    }

    partial void OnSelectedSessionChanged(AgentSessionItemViewModel? value)
    {
        SendCommand.NotifyCanExecuteChanged();
        if (value is null || IsBusy)
            return;

        FireAndForget(LoadSessionAsync(value), nameof(LoadSessionAsync));
    }

    partial void OnHasDocumentChanged(bool value) => UpdateBanner();

    private async Task InitializeAsync()
    {
        StatusText = S("Agent_Status_Ready", "Готов");
        await RefreshDocumentAndSessionsAsync().ConfigureAwait(true);
    }

    private async Task RefreshDocumentAndSessionsAsync()
    {
        if (_isRefreshing)
            return;

        if (IsBusy)
        {
            _refreshAfterTurn = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            var context = await _projectContextProvider
                .GetAsync(CancellationToken.None)
                .ConfigureAwait(true);

            _currentContext = context;
            HasDocument = context.HasDocument;
            DocumentTitle = context.HasDocument
                ? context.Title + " · Revit " + context.RevitVersion
                : S("Agent_Document_None", "Нет открытого проекта");

            if (!context.HasDocument)
            {
                Sessions.Clear();
                Messages.Clear();
                SelectedSession = null;
                TokenSummary = "—";
                if (!HasError)
                    StatusText = S("Agent_Status_NoDocument", "Откройте проект");
                return;
            }

            var currentId = SelectedSession?.Id;
            var sessions = await _conversationStore
                .ListConversationsAsync(context.DocumentId, 50, CancellationToken.None)
                .ConfigureAwait(true);

            Sessions.Clear();
            foreach (var session in sessions)
            {
                Sessions.Add(new AgentSessionItemViewModel(
                    session.Id,
                    session.Title,
                    session.UpdatedAt));
            }

            var target = currentId.HasValue
                ? Sessions.FirstOrDefault(item => item.Id == currentId.Value)
                : null;

            if (target is null && Sessions.Count > 0)
                target = Sessions[0];

            if (target is null)
            {
                await CreateSessionAsync().ConfigureAwait(true);
            }
            else
            {
                SelectedSession = target;
            }

            if (!HasError)
                StatusText = S("Agent_Status_Ready", "Готов");
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = S("Agent_Status_Error", "Ошибка") + ": " + ex.Message;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (!HasDocument || IsBusy)
            return;

        await CreateSessionAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void RefreshSessions()
    {
        if (IsBusy)
            return;

        FireAndForget(RefreshDocumentAndSessionsAsync(), nameof(RefreshDocumentAndSessionsAsync));
    }

    private async Task CreateSessionAsync()
    {
        if (_currentContext is not { HasDocument: true } context)
            return;

        var header = new AgentConversationHeader
        {
            Title = NewChatTitle,
            DocumentId = context.DocumentId,
            DocumentPath = context.PathName,
            RevitVersion = context.RevitVersion,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var id = await _conversationStore
            .CreateConversationAsync(header, CancellationToken.None)
            .ConfigureAwait(true);

        var item = new AgentSessionItemViewModel(id, header.Title, DateTime.UtcNow);
        Sessions.Insert(0, item);
        SelectedSession = item;
        Messages.Clear();
        _toolCards.Clear();
        StatusText = S("Agent_Status_NewChat", "Новый чат");
        HasError = false;
    }

    private async Task LoadSessionAsync(AgentSessionItemViewModel session)
    {
        try
        {
            var messages = await _conversationStore
                .GetMessagesAsync(session.Id, CancellationToken.None)
                .ConfigureAwait(true);

            Messages.Clear();
            _toolCards.Clear();
            RenderStoredMessages(messages);
            StatusText = messages.Count == 0
                ? S("Agent_Status_NewChat", "Новый чат")
                : S("Agent_Status_HistoryLoaded", "История загружена");
            HasError = false;
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = S("Agent_Status_Error", "Ошибка") + ": " + ex.Message;
        }
    }

    private void RenderStoredMessages(IReadOnlyList<AgentMessage> messages)
    {
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case AgentRole.User:
                    Messages.Add(new AgentChatItemViewModel(AgentChatItemKind.User)
                    {
                        Header = S("Agent_User", "Вы"),
                        Text = message.Content ?? string.Empty,
                        Attachments = CreateAttachmentPreviews(message.Attachments)
                    });
                    break;

                case AgentRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        Messages.Add(new AgentChatItemViewModel(AgentChatItemKind.Assistant)
                        {
                            Header = S("Agent_Assistant", "SmartCon Agent"),
                            Text = message.Content!
                        });
                    }

                    foreach (var call in message.ToolCalls)
                    {
                        var card = new AgentChatItemViewModel(AgentChatItemKind.Tool, call.Id)
                        {
                            Header = call.Name,
                            Text = S("Agent_Tool_Done", "выполнено"),
                            StatusText = "✓"
                        };
                        Messages.Add(card);
                        _toolCards[call.Id] = card;
                    }
                    break;

                case AgentRole.Tool:
                    if (message.ToolCallId is not null
                        && _toolCards.TryGetValue(message.ToolCallId, out var toolCard))
                    {
                        toolCard.Text = DescribeToolResult(message.Content);
                        toolCard.Details = AgentToolResultFormatter.Pretty(message.Content);
                        toolCard.StatusText = "✓ " + S("Agent_Tool_Done", "выполнено");
                        toolCard.IsSuccess = true;
                    }
                    break;
            }
        }
    }

    private static IReadOnlyList<AgentAttachmentItemViewModel> CreateAttachmentPreviews(
        IReadOnlyList<AgentAttachment> attachments)
    {
        if (attachments.Count == 0)
            return Array.Empty<AgentAttachmentItemViewModel>();

        var result = new List<AgentAttachmentItemViewModel>();
        foreach (var attachment in attachments)
        {
            try
            {
                result.Add(new AgentAttachmentItemViewModel(
                    attachment.FileName,
                    attachment.MimeType,
                    Convert.FromBase64String(attachment.Base64Data)));
            }
            catch (FormatException)
            {
            }
        }

        return result;
    }

    private static string DescribeToolResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json!);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text!;
            }

            if (root.TryGetProperty("errorCode", out var errorCode)
                && errorCode.ValueKind == JsonValueKind.String)
            {
                return "✗ " + errorCode.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return Truncate(json!, MaxStoredToolResultPreview);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Remove(maxLength) + "…";

    private void TouchCurrentSession()
    {
        if (SelectedSession is null)
            return;

        SelectedSession.UpdatedAt = DateTime.UtcNow;
        var index = Sessions.IndexOf(SelectedSession);
        if (index > 0)
            Sessions.Move(index, 0);
    }

    private void UpdateBanner()
    {
        ShowBanner = !HasDocument || !HasApiKey;
        BannerText = !HasDocument
            ? S("Agent_Banner_NoDocument", "Откройте проект Revit — агент получит контекст автоматически.")
            : S("Agent_Banner_NoApiKey", "Ключ LLM не настроен. Задайте SMARTCON_OPENROUTER_API_KEY.");
    }

    private void OnActiveDocumentChanged(object? sender, ActiveDocumentChangedEventArgs e)
    {
        // A turn started in the previous document must not keep writing into
        // the new one; cancel it and refresh sessions after it unwinds.
        _turnCancellation?.Cancel();

        FireAndForget(
            _dispatcher.InvokeAsync(() =>
            {
                FireAndForget(
                    RefreshDocumentAndSessionsAsync(),
                    nameof(RefreshDocumentAndSessionsAsync));
            }),
            nameof(OnActiveDocumentChanged));
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsChatEmpty));
    }

    private void OnAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAttachments));
    }

    private static string S(string key, string fallback) =>
        LanguageManager.GetString(key) ?? fallback;

    private static void FireAndForget(Task task, string operationName)
    {
        _ = task.ContinueWith(
            faulted => SmartCon.Core.Logging.SmartConLogger.Error(
                "Agent '" + operationName + "' failed: " + faulted.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}