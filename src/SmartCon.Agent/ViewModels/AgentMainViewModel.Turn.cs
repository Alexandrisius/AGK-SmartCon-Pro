using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;
using SmartCon.UI;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    private bool CanSend() =>
        !IsBusy
        && HasDocument
        && SelectedSession is not null
        && !string.IsNullOrWhiteSpace(InputText);

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || SelectedSession is null)
            return;

        var attachmentItems = Attachments.ToList();
        var attachments = attachmentItems.Select(item => item.ToAttachment()).ToList();
        var userText = text;

        Messages.Add(new AgentChatItemViewModel(AgentChatItemKind.User)
        {
            Header = S("Agent_User", "Вы"),
            Text = userText,
            Attachments = attachmentItems
        });

        InputText = string.Empty;
        Attachments.Clear();
        _toolCards.Clear();
        _currentAssistant = null;

        var session = SelectedSession;
        _turnCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsTyping = true;
        IsStopping = false;
        HasError = false;
        StatusText = S("Agent_Status_Sending", "Отправка…");

        try
        {
            await UpdateTitleIfDefaultAsync(session, text, _turnCancellation.Token)
                .ConfigureAwait(true);

            var progress = new Progress<AgentProgress>(OnProgress);
            var result = await _orchestrator.RunTurnAsync(
                    session.Id,
                    text,
                    attachments,
                    progress,
                    _turnCancellation.Token)
                .ConfigureAwait(true);

            if (_currentAssistant is not null)
                _currentAssistant.IsStreaming = false;

            UpdateUsage(result.Usage);
            StatusText = result.MaxRoundsReached
                ? S("Agent_Status_StepLimit", "Достигнут лимит шагов")
                : S("Agent_Status_Ready", "Готов");
            TouchCurrentSession();
        }
        catch (OperationCanceledException)
        {
            if (_currentAssistant is not null)
            {
                _currentAssistant.IsStreaming = false;
                _currentAssistant.Text += "\n\n" + S("Agent_Stop_Suffix", "(остановлено)");
            }

            AddInfo(S("Agent_Status_Stopped", "Остановлено"));
            StatusText = S("Agent_Status_Stopped", "Остановлено");
        }
        catch (Exception ex)
        {
            AddError(ex.Message);
            HasError = true;
            StatusText = S("Agent_Status_Error", "Ошибка");
        }
        finally
        {
            HideConfirmation();

            if (_currentAssistant is not null)
                _currentAssistant.IsStreaming = false;

            IsBusy = false;
            IsTyping = false;
            IsStopping = false;
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            _currentAssistant = null;
            SendCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();

            if (_refreshAfterTurn)
            {
                _refreshAfterTurn = false;
                FireAndForget(
                    RefreshDocumentAndSessionsAsync(),
                    nameof(RefreshDocumentAndSessionsAsync));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_turnCancellation is null)
            return;

        IsStopping = true;
        StatusText = S("Agent_Status_Stopping", "Останавливаю…");
        _turnCancellation.Cancel();
    }

    private bool CanPrimaryAction() => IsBusy || CanSend();

    /// <summary>
    /// One composer action: while a turn is running the primary button is Stop;
    /// when idle it is Send. When steer lands, a non-empty input during a turn
    /// will switch this back to Send.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrimaryAction))]
    private async Task PrimaryActionAsync()
    {
        if (IsBusy)
        {
            Stop();
            return;
        }

        if (CanSend())
            await SendAsync();
    }

    [RelayCommand]
    private void ToggleContextPopup() => IsContextPopupOpen = !IsContextPopupOpen;

    private void OnProgress(AgentProgress progress)
    {
        switch (progress.Kind)
        {
            case AgentProgressKind.Status:
                IsTyping = true;
                StatusText = progress.Text ?? "…";
                break;

            case AgentProgressKind.TextDelta:
                IsTyping = false;
                if (_currentAssistant is null)
                {
                    _currentAssistant = new AgentChatItemViewModel(AgentChatItemKind.Assistant)
                    {
                        Header = S("Agent_Assistant", "SmartCon Agent"),
                        IsStreaming = true
                    };
                    Messages.Add(_currentAssistant);
                }

                _currentAssistant.Text += progress.Text ?? string.Empty;
                break;

            case AgentProgressKind.ReasoningDelta:
                StatusText = S("Agent_Status_Thinking", "Думаю…");
                break;

            case AgentProgressKind.ToolStarted:
                IsTyping = false;
                if (_currentAssistant is not null)
                    _currentAssistant.IsStreaming = false;
                _currentAssistant = null;
                var toolCard = new AgentChatItemViewModel(AgentChatItemKind.Tool, progress.ToolCallId)
                {
                    Header = progress.ToolName ?? "tool",
                    Text = S("Agent_Tool_Running", "Выполняется…"),
                    StatusText = "…",
                    IsStreaming = true
                };
                Messages.Add(toolCard);
                if (progress.ToolCallId is not null)
                    _toolCards[progress.ToolCallId] = toolCard;

                StatusText = Format("Agent_Status_ToolPrefix", "Инструмент: {0}", progress.ToolName ?? "");
                break;

            case AgentProgressKind.ToolCompleted:
                if (progress.ToolCallId is not null
                    && _toolCards.TryGetValue(progress.ToolCallId, out var completedCard))
                {
                    completedCard.IsStreaming = false;
                    completedCard.IsSuccess = progress.Success;
                    completedCard.StatusText = progress.Success
                        ? "✓ " + S("Agent_Tool_Done", "выполнено")
                        : "✗ " + (progress.Error ?? S("Agent_Tool_Error", "ошибка"));
                    if (!string.IsNullOrWhiteSpace(progress.Text))
                        completedCard.Text = progress.Text!;
                    if (!string.IsNullOrWhiteSpace(progress.Details))
                        completedCard.Details = progress.Details!;
                }

                StatusText = progress.Success
                    ? S("Agent_Status_ToolSuccess", "Продолжаю…")
                    : S("Agent_Status_ToolFailure", "Инструмент вернул ошибку");
                break;

            case AgentProgressKind.TurnCompleted:
                if (_currentAssistant is not null)
                    _currentAssistant.IsStreaming = false;
                if (progress.Usage is not null)
                    UpdateUsage(progress.Usage);
                break;

            case AgentProgressKind.ContextUsage:
                ContextLimit = progress.ContextLimit;
                ContextTokens = progress.ContextTokens;
                UpdateContextDetails(progress);
                break;

            case AgentProgressKind.Error:
                AddError(progress.Error ?? S("Agent_Status_Error", "Ошибка"));
                HasError = true;
                StatusText = S("Agent_Status_Error", "Ошибка");
                break;
        }
    }

    private async Task UpdateTitleIfDefaultAsync(
        AgentSessionItemViewModel session,
        string text,
        CancellationToken ct)
    {
        if (!string.Equals(session.Title, NewChatTitle, StringComparison.Ordinal))
            return;

        var title = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length > 60)
            title = title.Remove(60) + "…";

        try
        {
            await _conversationStore.UpdateTitleAsync(session.Id, title, ct)
                .ConfigureAwait(true);
            session.Title = title;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            session.Title = title;
        }
    }

    private void UpdateUsage(AgentUsage usage)
    {
        if (ReferenceEquals(usage, AgentUsage.Empty))
            return;

        var parts = new List<string>(3);
        if (usage.CompletionTokens > 0)
            parts.Add("out " + FormatTokens(usage.CompletionTokens));
        if (usage.CostUsd.HasValue)
            parts.Add("$" + usage.CostUsd.Value.ToString("0.0000", CultureInfo.InvariantCulture));

        if (parts.Count > 0)
            TokenSummary = string.Join(" · ", parts);
    }

    private void UpdateContextDetails(AgentProgress progress)
    {
        if (string.IsNullOrEmpty(progress.CompactionKind))
            return;

        var label = progress.CompactionKind == "summary"
            ? S("Agent_Context_Summary", "История сжата в чекпойнт")
            : progress.CompactionKind == "hard_shrink"
                ? S("Agent_Context_HardShrink", "Аварийное усечение контекста")
                : S("Agent_Context_Masked", "Старые результаты инструментов заменены дайджестами");

        var parts = new List<string> { label };
        if (progress.CompactionMasked > 0)
            parts.Add("×" + progress.CompactionMasked);
        if (progress.CompactionDropped > 0)
            parts.Add("−" + progress.CompactionDropped);
        if (progress.CompactionTokensBefore > 0)
        {
            parts.Add(FormatNumber(progress.CompactionTokensBefore) + " → " +
                      FormatNumber(progress.CompactionTokensAfter));
        }

        ContextDetails = string.Join(" · ", parts);
    }

    private static string FormatTokens(int value) =>
        value >= 1000
            ? (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K"
            : value.ToString(CultureInfo.InvariantCulture);

    private void AddError(string message)
    {
        Messages.Add(new AgentChatItemViewModel(AgentChatItemKind.Error)
        {
            Header = S("Agent_Error", "Ошибка"),
            Text = message
        });
    }

    private void AddInfo(string message)
    {
        Messages.Add(new AgentChatItemViewModel(AgentChatItemKind.Info)
        {
            Text = message
        });
    }

    private static string Format(string key, string fallback, string value)
    {
        var format = LanguageManager.GetString(key) ?? fallback;
        return string.Format(CultureInfo.InvariantCulture, format, value);
    }
}