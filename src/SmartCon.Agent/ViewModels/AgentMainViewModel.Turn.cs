using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;
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
        _lastAssistantWithText = null;
        _turnWorkItems.Clear();
        _turnToolCalls = 0;
        _turnStartedUtc = DateTime.UtcNow;

        var session = SelectedSession;
        _turnCancellation = new CancellationTokenSource();
        IsBusy = true;
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
            {
                FinalizeReasoning(_currentAssistant);
                _currentAssistant.IsStreaming = false;
            }

            AddTurnCost(result.Usage);
            FinishTurn(result, session.Id);
            StatusText = result.MaxRoundsReached
                ? S("Agent_Status_StepLimit", "Достигнут лимит шагов")
                : S("Agent_Status_Ready", "Готов");
            TouchCurrentSession();
        }
        catch (OperationCanceledException)
        {
            if (_currentAssistant is not null)
            {
                FinalizeReasoning(_currentAssistant);
                _currentAssistant.IsStreaming = false;
                _currentAssistant.Text += "\n\n" + S("Agent_Stop_Suffix", "(остановлено)");
                _lastAssistantWithText = _currentAssistant;
            }

            AddInfo(S("Agent_Status_Stopped", "Остановлено"));
            FinishTurn(null, session.Id, stopped: true);
            StatusText = S("Agent_Status_Stopped", "Остановлено");
        }
        catch (Exception ex)
        {
            AddError(ex.Message);
            HasError = true;
            FinishTurn(null, session.Id);
            StatusText = S("Agent_Status_Error", "Ошибка");
        }
        finally
        {
            HideConfirmation();

            if (_currentAssistant is not null)
            {
                FinalizeReasoning(_currentAssistant);
                _currentAssistant.IsStreaming = false;
            }

            IsBusy = false;
            IsStopping = false;

            // Exchange-then-dispose: a concurrent Stop/OnActiveDocumentChanged
            // may hold the reference, so Cancel must never hit a disposed source.
            var cancellation = Interlocked.Exchange(ref _turnCancellation, null);
            cancellation?.Dispose();
            _currentAssistant = null;
            _lastAssistantWithText = null;
            _turnWorkItems.Clear();
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
        CancelTurn();
        CancelActiveJob("Stop");
    }

    private void CancelTurn()
    {
        var cancellation = _turnCancellation;
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished and disposed its source between the read and Cancel.
        }
    }

    /// <summary>
    /// A bulk job runs on its own ExternalEvent and is not tied to the turn
    /// token: pressing Stop (or switching to read-only) stopped the LLM, while
    /// the pump kept writing to the model. Cancel the active job explicitly.
    /// </summary>
    private void CancelActiveJob(string reason)
    {
        if (_jobScheduler is null || _jobStore is null)
            return;

        var documentId = _currentContext?.DocumentId ?? _sessionContext?.DocumentId;
        if (string.IsNullOrEmpty(documentId))
            return;

        try
        {
            var active = AsyncBridge.RunSync(
                () => _jobStore.GetActiveAsync(documentId!, CancellationToken.None));
            if (active is null || JobStateMachine.IsTerminal(active.State))
                return;

            _jobScheduler.Cancel(active.JobId, documentId!);
            SmartConLogger.Info(
                "Agent: " + reason + " cancelled the active job " + active.JobId.ToString("D"));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent: could not cancel the active job on " + reason + ": " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: the job may still be running; call cancel_job]");
        }
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
                StatusText = progress.Text ?? "…";
                break;

            case AgentProgressKind.TextDelta:
                var textItem = EnsureAssistant();
                if (textItem.IsReasoningStreaming)
                    FinalizeReasoning(textItem);

                textItem.Text += progress.Text ?? string.Empty;
                _lastAssistantWithText = textItem;
                break;

            case AgentProgressKind.ReasoningDelta:
                AppendReasoning(progress.Text);
                StatusText = S("Agent_Status_Thinking", "Думаю…");
                break;

            case AgentProgressKind.ToolStarted:
                if (_currentAssistant is not null)
                {
                    FinalizeReasoning(_currentAssistant);
                    _currentAssistant.IsStreaming = false;
                    _currentAssistant = null;
                }

                var toolLabel = AgentToolDisplayNames.Get(progress.ToolName);
                if (string.IsNullOrEmpty(toolLabel))
                    toolLabel = "tool";
                var toolCard = new AgentChatItemViewModel(AgentChatItemKind.Tool, progress.ToolCallId)
                {
                    Header = toolLabel,
                    ToolName = progress.ToolName,
                    Text = S("Agent_Tool_Running", "Выполняется…"),
                    StatusText = "…",
                    IsStreaming = true,
                    IsWorkItem = true
                };
                Messages.Add(toolCard);
                _turnWorkItems.Add(toolCard);
                _turnToolCalls++;
                if (progress.ToolCallId is not null)
                    _toolCards[progress.ToolCallId] = toolCard;

                StatusText = Format("Agent_Status_ToolPrefix", "Инструмент: {0}", toolLabel);
                break;

            case AgentProgressKind.ToolCompleted:
                var completedCard = FindToolCard(progress);
                if (completedCard is not null)
                {
                    completedCard.IsStreaming = false;
                    completedCard.IsSuccess = progress.Success;
                    completedCard.StatusText = progress.Success
                        ? "✓ " + S("Agent_Tool_Done", "выполнено")
                        : "✗ " + (progress.Error ?? S("Agent_Tool_Error", "ошибка"));
                    if (!string.IsNullOrWhiteSpace(progress.Text))
                        completedCard.Text = progress.Text!;
                    if (!string.IsNullOrWhiteSpace(progress.Details))
                    {
                        completedCard.Details = WithToolName(
                            completedCard.ToolName ?? progress.ToolName,
                            progress.Details!);
                    }
                }

                StatusText = progress.Success
                    ? S("Agent_Status_ToolSuccess", "Продолжаю…")
                    : S("Agent_Status_ToolFailure", "Инструмент вернул ошибку");
                break;

            case AgentProgressKind.TurnCompleted:
                if (_currentAssistant is not null)
                {
                    FinalizeReasoning(_currentAssistant);
                    _currentAssistant.IsStreaming = false;
                }

                // The final text normally arrives as deltas. A turn that was
                // aborted by the loop guard (or a provider that returns text
                // only in the completion) has no delta at all — without this
                // the user saw a silent stop with "Ready".
                if (!string.IsNullOrWhiteSpace(progress.Text)
                    && (_currentAssistant is null
                        || string.IsNullOrWhiteSpace(_currentAssistant.Text)))
                {
                    var finalItem = EnsureAssistant();
                    finalItem.Text = progress.Text!;
                    finalItem.IsStreaming = false;
                    _lastAssistantWithText = finalItem;
                }

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

        var title = AgentConversationTitle.FromFirstMessage(text);

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

    private AgentChatItemViewModel? FindToolCard(AgentProgress progress)
    {
        if (progress.ToolCallId is not null
            && _toolCards.TryGetValue(progress.ToolCallId, out var exact))
        {
            return exact;
        }

        // Some providers emit the tool name before the id: the start event then
        // carries a synthetic id while the completion carries the real one.
        if (!string.IsNullOrEmpty(progress.ToolName))
        {
            foreach (var item in Messages)
            {
                if (item.IsTool
                    && item.IsStreaming
                    && string.Equals(item.ToolName, progress.ToolName, StringComparison.Ordinal))
                {
                    return item;
                }
            }
        }

        return null;
    }

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

    private static string FormatMany(string key, string fallback, params object[] values)
    {
        var format = LanguageManager.GetString(key) ?? fallback;
        return string.Format(CultureInfo.InvariantCulture, format, values);
    }
}