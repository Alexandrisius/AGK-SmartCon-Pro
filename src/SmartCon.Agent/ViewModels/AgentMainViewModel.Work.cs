using System.Globalization;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;
using SmartCon.UI;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    private AgentChatItemViewModel EnsureAssistant()
    {
        if (_currentAssistant is not null)
            return _currentAssistant;

        _currentAssistant = new AgentChatItemViewModel(AgentChatItemKind.Assistant)
        {
            Header = S("Agent_Assistant", "BIM Agent"),
            IsStreaming = true,
            IsWorkItem = true
        };
        Messages.Add(_currentAssistant);
        _turnWorkItems.Add(_currentAssistant);
        return _currentAssistant;
    }

    private void AppendReasoning(string? delta)
    {
        var item = EnsureAssistant();
        if (string.IsNullOrEmpty(delta))
            return;

        item.ReasoningHeader = S("Agent_Reasoning", "Размышления");
        item.ReasoningText += delta;
        item.ReasoningStatus = S("Agent_Reasoning_Streaming", "Размышляю…");
        item.IsReasoningStreaming = true;
        item.IsReasoningExpanded = true;
    }

    private static void FinalizeReasoning(AgentChatItemViewModel item)
    {
        item.IsReasoningStreaming = false;
        item.ReasoningStatus = string.Empty;
        if (item.HasReasoning)
            item.IsReasoningExpanded = false;
    }

    private void FinishTurn(AgentTurnResult? result, Guid sessionId, bool stopped = false)
    {
        var elapsed = DateTime.UtcNow - _turnStartedUtc;
        var tools = result?.ToolCallCount ?? _turnToolCalls;

        CollapseTurnWork(elapsed, tools, stopped);
        FireAndForget(
            RefreshSessionMetricsAsync(sessionId),
            nameof(RefreshSessionMetricsAsync));
    }

    private void CollapseTurnWork(TimeSpan elapsed, int toolCalls, bool stopped = false)
    {
        var work = _turnWorkItems.Distinct().ToList();
        if (work.Count == 0)
            return;

        var final = _lastAssistantWithText;
        if (final is null || !work.Contains(final))
            final = work.LastOrDefault(item => item.IsAssistant);

        var hidden = work
            .Where(item => !ReferenceEquals(item, final))
            .ToList();
        if (final is not null)
        {
            final.IsWorkItem = false;
            final.IsVisible = true;
        }

        if (hidden.Count == 0)
            return;

        foreach (var item in hidden)
            item.IsVisible = false;

        // The final answer's reasoning line folds into the work row too, so the
        // collapsed transcript reads "question + answer" only (owner feedback).
        if (final is not null && final.HasReasoning)
            final.IsReasoningVisible = false;

        var summary = CreateWorkSummary(
            hidden,
            toolCalls,
            elapsed,
            stopped,
            reasoningOwner: final is not null && final.HasReasoning ? final : null,
            history: false);
        // ZCode-style: the disclosure row leads the turn, above the hidden steps,
        // so an expanded transcript can be collapsed back by the same row.
        var index = Messages.IndexOf(hidden[0]);
        if (index >= 0)
            Messages.Insert(index, summary);
        else
            Messages.Add(summary);
    }

    private AgentChatItemViewModel CreateWorkSummary(
        IReadOnlyList<AgentChatItemViewModel> hidden,
        int toolCalls,
        TimeSpan elapsed,
        bool stopped,
        AgentChatItemViewModel? reasoningOwner,
        bool history)
    {
        // Elapsed time and tool calls live ONLY here (owner feedback): never a
        // second metrics line under the answer.
        string text;
        if (history)
        {
            text = FormatMany(
                "Agent_Work_Summary_History",
                "Вызвано инструментов: {0}",
                toolCalls > 0 ? toolCalls : hidden.Count);
        }
        else if (toolCalls > 0)
        {
            text = FormatMany(
                "Agent_Work_Summary",
                "Время работы: {0}, вызвано инструментов: {1}",
                FormatWorkDuration(elapsed),
                toolCalls);
        }
        else
        {
            text = FormatMany(
                "Agent_Work_Summary_TimeOnly",
                "Время работы: {0}",
                FormatWorkDuration(elapsed));
        }

        if (stopped)
            text = S("Agent_Status_Stopped", "Остановлено") + " · " + text;

        var details = FormatMany(
            "Agent_Work_Details",
            "Шагов скрыто: {0}, из них вызовов инструментов: {1}",
            hidden.Count,
            toolCalls);

        return new AgentChatItemViewModel(AgentChatItemKind.WorkSummary)
        {
            Header = S("Agent_Work", "Работа агента"),
            Text = text,
            Details = details,
            WorkItems = hidden,
            ReasoningOwner = reasoningOwner
        };
    }

    /// <summary>
    /// "23 мин 39 с" / "1 ч 5 мин" — wall-clock words instead of the mm:ss clock
    /// format used by the session line.
    /// </summary>
    private string FormatWorkDuration(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        var secondsSuffix = S("Agent_Duration_S", " с");
        var minutesSuffix = S("Agent_Duration_M", " мин");
        var hoursSuffix = S("Agent_Duration_H", " ч");

        if (seconds < 60)
            return seconds.ToString(CultureInfo.InvariantCulture) + secondsSuffix;

        var minutes = seconds / 60;
        var restSeconds = seconds % 60;
        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + minutesSuffix +
                   (restSeconds > 0
                       ? " " + restSeconds.ToString(CultureInfo.InvariantCulture) + secondsSuffix
                       : string.Empty);
        }

        var hours = minutes / 60;
        var restMinutes = minutes % 60;
        return hours.ToString(CultureInfo.InvariantCulture) + hoursSuffix +
               (restMinutes > 0
                   ? " " + restMinutes.ToString(CultureInfo.InvariantCulture) + minutesSuffix
                   : string.Empty);
    }

    private async Task RefreshSessionMetricsAsync(Guid sessionId)
    {
        if (_usageStore is null)
        {
            SessionMetricsText = string.Empty;
            SessionMetricsDetails = string.Empty;
            return;
        }

        try
        {
            var metrics = await _usageStore
                .GetSessionMetricsAsync(sessionId, CancellationToken.None)
                .ConfigureAwait(true);
            if (SelectedSession?.Id != sessionId)
                return;

            var (text, details) = FormatSessionMetrics(metrics);
            SessionMetricsText = text;
            SessionMetricsDetails = details;
        }
        catch (Exception)
        {
            // The analytics line is informational: never break the chat.
            SessionMetricsText = string.Empty;
            SessionMetricsDetails = string.Empty;
        }
    }

    private static (string Text, string Details) FormatSessionMetrics(AgentSessionMetrics? metrics)
    {
        if (metrics is null || metrics.IsEmpty)
            return (string.Empty, string.Empty);

        var text = FormatMany(
            "Agent_Metrics_Session",
            "⏱ {0} · {1} инструментов",
            FormatElapsed(TimeSpan.FromMilliseconds(metrics.LlmLatencyMs + metrics.ToolDurationMs)),
            metrics.ToolCalls);
        var details = FormatMany(
            "Agent_Metrics_Session_Details",
            "ходов {0} · LLM {1} · инструменты {2} · {3} → {4} токенов · {5}",
            metrics.Turns,
            FormatElapsed(TimeSpan.FromMilliseconds(metrics.LlmLatencyMs)),
            FormatElapsed(TimeSpan.FromMilliseconds(metrics.ToolDurationMs)),
            FormatCompactSize(metrics.PromptTokens),
            FormatCompactSize(metrics.CompletionTokens),
            FormatCost(metrics.CostUsd));
        return (text, details);
    }

    private static string FormatCost(decimal? cost) =>
        "$" + (cost ?? 0m).ToString("0.0000", CultureInfo.InvariantCulture);

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        if (seconds < 60)
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        var minutes = (int)(seconds / 60);
        return minutes.ToString(CultureInfo.InvariantCulture) + ":" +
               ((int)(seconds % 60)).ToString("00", CultureInfo.InvariantCulture);
    }

}
