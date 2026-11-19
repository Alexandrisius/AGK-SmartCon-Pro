using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.UI;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Agent;
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
    private readonly AgentDecisionsSettings _decisionsSettings;
    private readonly IAgentFilePicker _filePicker;
    private readonly IAgentClipboardService _clipboardService;
    private readonly AgentConfirmationService _confirmationService;
    private readonly IActiveDocumentChangeNotifier? _documentChangeNotifier;
    private readonly IAgentUsageStore? _usageStore;
    private readonly IDispatcher _dispatcher;
    private readonly DecisionCostTracker? _decisionCostTracker;
    private readonly object _costGate = new();
    private bool _hasApiKey;
    private decimal _chatCostUsd;
    private decimal _decisionCostUsd;
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
        AgentDecisionsSettings decisionsSettings,
        IAgentFilePicker filePicker,
        IAgentClipboardService clipboardService,
        AgentConfirmationService confirmationService,
        IDispatcher dispatcher,
        Services.Tools.AgentSessionContext sessionContext,
        IActiveDocumentChangeNotifier? documentChangeNotifier = null,
        IAgentUsageStore? usageStore = null,
        DecisionCostTracker? decisionCostTracker = null)
    {
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _projectContextProvider = projectContextProvider
            ?? throw new ArgumentNullException(nameof(projectContextProvider));
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _decisionsSettings = decisionsSettings
            ?? throw new ArgumentNullException(nameof(decisionsSettings));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _confirmationService = confirmationService
            ?? throw new ArgumentNullException(nameof(confirmationService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _documentChangeNotifier = documentChangeNotifier;
        _usageStore = usageStore;
        _decisionCostTracker = decisionCostTracker;
        _hasApiKey = settings.HasApiKey;

        if (_decisionCostTracker is not null)
            _decisionCostTracker.CostAdded += OnDecisionCostAdded;

        AttachConfirmationService(_confirmationService);
        AttachSessionContext(sessionContext);
        Messages.CollectionChanged += OnMessagesChanged;
        Attachments.CollectionChanged += OnAttachmentsChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
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
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusyVisible))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTooltip))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isTyping;

    [ObservableProperty]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private bool _hasDocument;

    [ObservableProperty]
    private bool _showBanner;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    [ObservableProperty]
    private string _documentTitle = string.Empty;

    [ObservableProperty]
    private string _tokenSummary = "$0.0000";

    [ObservableProperty]
    private int _contextTokens;

    [ObservableProperty]
    private int _contextLimit;

    [ObservableProperty]
    private string _contextNumbers = string.Empty;

    [ObservableProperty]
    private string _contextTooltip = string.Empty;

    [ObservableProperty]
    private string _contextDetails = string.Empty;

    [ObservableProperty]
    private bool _isContextPopupOpen;

    public bool HasContextUsage => ContextLimit > 0;

    public string PrimaryActionTooltip =>
        IsBusy ? S("Agent_Stop", "Стоп") : S("Agent_Send", "Отправить");

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

        if (_decisionCostTracker is not null)
            _decisionCostTracker.CostAdded -= OnDecisionCostAdded;

        DetachConfirmationService(_confirmationService);
        Messages.CollectionChanged -= OnMessagesChanged;
        Attachments.CollectionChanged -= OnAttachmentsChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        _turnCancellation = null;
    }

    partial void OnSelectedSessionChanged(AgentSessionItemViewModel? value)
    {
        SendCommand.NotifyCanExecuteChanged();
        PrimaryActionCommand.NotifyCanExecuteChanged();
        ResetContextUsage();
        ResetSessionCost();
        if (value is null || IsBusy)
            return;

        FireAndForget(LoadSessionAsync(value), nameof(LoadSessionAsync));
    }

    partial void OnHasDocumentChanged(bool value) => UpdateBanner();

    partial void OnContextTokensChanged(int value) => UpdateContextPresentation();

    partial void OnContextLimitChanged(int value) => UpdateContextPresentation();

    private void ResetContextUsage()
    {
        ContextTokens = 0;
        ContextLimit = 0;
        ContextDetails = string.Empty;
        ContextNumbers = string.Empty;
        IsContextPopupOpen = false;
    }

    private void ResetSessionCost()
    {
        lock (_costGate)
        {
            _chatCostUsd = 0m;
            _decisionCostUsd = 0m;
        }

        UpdateTokenSummary();
    }

    private void ApplySessionCost(decimal costUsd)
    {
        lock (_costGate)
            _chatCostUsd = costUsd;

        UpdateTokenSummary();
    }

    private void AddTurnCost(AgentUsage usage)
    {
        if (!usage.CostUsd.HasValue)
            return;

        lock (_costGate)
            _chatCostUsd += usage.CostUsd.Value;

        UpdateTokenSummary();
    }

    private void OnDecisionCostAdded(decimal delta)
    {
        lock (_costGate)
            _decisionCostUsd += delta;

        // Decision calls complete on the tool-execution thread; the counter is
        // WPF-bound and must be updated on the UI thread.
        FireAndForget(_dispatcher.InvokeAsync(UpdateTokenSummary), nameof(OnDecisionCostAdded));
    }

    private void UpdateTokenSummary()
    {
        decimal total;
        lock (_costGate)
            total = _chatCostUsd + _decisionCostUsd;

        TokenSummary = "$" + total.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    private async Task RefreshSessionCostAsync(Guid sessionId)
    {
        if (_usageStore is null)
            return;

        try
        {
            var cost = await _usageStore
                .GetSessionCostAsync(sessionId, CancellationToken.None)
                .ConfigureAwait(true);
            ApplySessionCost(cost);
        }
        catch (Exception)
        {
            // The cost line is informational: never break the chat because the
            // usage table could not be read.
            ApplySessionCost(0m);
        }
    }

    /// <summary>
    /// Restores the context donut of a stored conversation. Tokens and the
    /// compaction trigger are persisted with every turn, so switching sessions
    /// or restarting Revit shows the same fill level immediately.
    /// </summary>
    private async Task RestoreContextUsageAsync(Guid sessionId)
    {
        if (_usageStore is null)
            return;

        try
        {
            var snapshot = await _usageStore
                .GetLastContextAsync(sessionId, CancellationToken.None)
                .ConfigureAwait(true);

            if (snapshot is { Tokens: > 0, Limit: > 0 })
            {
                ContextLimit = snapshot.Limit;
                ContextTokens = snapshot.Tokens;
                return;
            }
        }
        catch (Exception)
        {
            // The donut is informational: never break session loading.
        }

        ResetContextUsage();
    }

    private void UpdateContextPresentation()
    {
        if (ContextLimit <= 0)
        {
            ContextNumbers = string.Empty;
            ContextTooltip = S("Agent_Context_Unknown", "Контекст ещё не измерен");
            OnPropertyChanged(nameof(HasContextUsage));
            return;
        }

        var ratio = Math.Max(0, (double)ContextTokens / ContextLimit);
        ContextNumbers = FormatCompactSize(ContextTokens) + " / " +
                         FormatCompactSize(ContextLimit) + " (" +
                         (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
        ContextTooltip = S("Agent_Context", "Контекст") + ": ≈ " +
                         FormatNumber(ContextTokens) + " / " + FormatNumber(ContextLimit) +
                         " (" + (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
        OnPropertyChanged(nameof(HasContextUsage));
    }

    private static string FormatCompactSize(int value) =>
        value >= 1_000_000
            ? (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : value >= 1_000
                ? (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
                : value.ToString(CultureInfo.InvariantCulture);

    private static string FormatNumber(int value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);

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
                TokenSummary = "$0.0000";
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
                    session.UpdatedAt,
                    RequestDeleteSession,
                    ConfirmDeleteSession,
                    CancelDeleteSession));
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

    private void RequestDeleteSession(AgentSessionItemViewModel session)
    {
        if (IsBusy)
            return;

        foreach (var item in Sessions)
            item.IsConfirmingDelete = ReferenceEquals(item, session);

        StatusText = S("Agent_Session_DeleteConfirmHint",
            "Нажмите галочку, чтобы удалить сессию");
        HasError = false;
    }

    private void CancelDeleteSession(AgentSessionItemViewModel session)
    {
        session.IsConfirmingDelete = false;
        StatusText = S("Agent_Status_Ready", "Готов");
    }

    private void ConfirmDeleteSession(AgentSessionItemViewModel session)
    {
        FireAndForget(DeleteSessionAsync(session), nameof(DeleteSessionAsync));
    }

    private async Task DeleteSessionAsync(AgentSessionItemViewModel session)
    {
        if (IsBusy || !Sessions.Contains(session))
            return;

        var wasSelected = ReferenceEquals(SelectedSession, session);
        var index = Sessions.IndexOf(session);

        try
        {
            await _conversationStore
                .DeleteConversationAsync(session.Id, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = S("Agent_Status_Error", "Ошибка") + ": " + ex.Message;
            session.IsConfirmingDelete = false;
            return;
        }

        session.IsConfirmingDelete = false;
        Sessions.Remove(session);
        HasError = false;
        StatusText = S("Agent_Session_Deleted", "Сессия удалена");

        if (!wasSelected)
            return;

        if (Sessions.Count > 0)
        {
            SelectedSession = Sessions[Math.Min(index, Sessions.Count - 1)];
            return;
        }

        SelectedSession = null;
        Messages.Clear();
        _toolCards.Clear();
        ResetContextUsage();
        ResetSessionCost();
        await CreateSessionAsync().ConfigureAwait(true);
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

        var item = new AgentSessionItemViewModel(
            id,
            header.Title,
            DateTime.UtcNow,
            RequestDeleteSession,
            ConfirmDeleteSession,
            CancelDeleteSession);
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

            await RefreshSessionCostAsync(session.Id).ConfigureAwait(true);
            await RestoreContextUsageAsync(session.Id).ConfigureAwait(true);

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
                            Header = S("Agent_Assistant", "BIM Agent"),
                            Text = message.Content!
                        });
                    }

                    foreach (var call in message.ToolCalls)
                    {
                        var card = new AgentChatItemViewModel(AgentChatItemKind.Tool, call.Id)
                        {
                            Header = AgentToolDisplayNames.Get(call.Name),
                            ToolName = call.Name,
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
                        toolCard.Details = WithToolName(
                            toolCard.ToolName,
                            AgentToolResultFormatter.Pretty(message.Content));
                        toolCard.StatusText = "✓ " + S("Agent_Tool_Done", "выполнено");
                        toolCard.IsSuccess = true;
                    }
                    break;
            }
        }
    }

    private static string WithToolName(string? toolName, string details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return details;

        return string.IsNullOrWhiteSpace(toolName)
            ? details
            : "tool: " + toolName + "\n\n" + details;
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
            : S("Agent_Banner_NoApiKey",
                "Ключ LLM не настроен. Откройте настройки агента (⚙) и задайте ключ провайдера.");
    }

    private void OnActiveDocumentChanged(object? sender, ActiveDocumentChangedEventArgs e)
    {
        // A turn started in the previous document must not keep writing into
        // the new one; cancel it and refresh sessions after it unwinds.
        _turnCancellation?.Cancel();

        FireAndForget(
            _dispatcher.InvokeAsync(() =>
            {
                // Bypass / read-only choices are bound to one document (C1);
                // the session-mode property is WPF-bound, so reset on the UI thread.
                ResetApprovalModeForDocumentChange();
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