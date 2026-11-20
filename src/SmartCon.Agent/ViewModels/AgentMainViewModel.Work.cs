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
        var usage = result?.Usage ?? AgentUsage.Empty;
        var tools = result?.ToolCallCount ?? _turnToolCalls;
        var rounds = result?.Rounds ?? 0;

        AddTurnMetrics(elapsed, tools, rounds, usage, stopped);
        CollapseTurnWork(elapsed, tools);
        FireAndForget(
            RefreshSessionMetricsAsync(sessionId),
            nameof(RefreshSessionMetricsAsync));
    }

    private void AddTurnMetrics(
        TimeSpan elapsed,
        int toolCalls,
        int rounds,
        AgentUsage usage,
        bool stopped)
    {
        var text = FormatMany(
            "Agent_Metrics_Turn",
            "⏱ {0} · {1} тулов · {2} раундов · {3} → {4} токенов · {5}",
            FormatElapsed(elapsed),
            toolCalls,
            rounds,
            FormatCompactSize(usage.PromptTokens),
            FormatCompactSize(usage.CompletionTokens),
            FormatCost(usage.CostUsd));
        if (stopped)
            text = S("Agent_Status_Stopped", "Остановлено") + " · " + text;

        var totalTokens = usage.TotalTokens > 0
            ? usage.TotalTokens
            : usage.PromptTokens + usage.CompletionTokens;
        var details = FormatMany(
            "Agent_Metrics_Details",
            "prompt {0} · completion {1} · reasoning {2} · cached {3} · total {4}",
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.ReasoningTokens,
            usage.CachedTokens,
            totalTokens);

        Messages.Add(new AgentChatItemViewModel(AgentChatItemKind.Metrics)
        {
            Text = text,
            Details = details
        });
    }

    private void CollapseTurnWork(TimeSpan elapsed, int toolCalls)
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

        var summary = CreateWorkSummary(hidden, toolCalls, elapsed, history: false);
        var index = final is not null ? Messages.IndexOf(final) : -1;
        if (index >= 0)
            Messages.Insert(index, summary);
        else
            Messages.Add(summary);
    }

    private AgentChatItemViewModel CreateWorkSummary(
        IReadOnlyList<AgentChatItemViewModel> hidden,
        int toolCalls,
        TimeSpan elapsed,
        bool history)
    {
        var actions = toolCalls > 0 ? toolCalls : hidden.Count;
        var text = history
            ? FormatMany("Agent_Work_Summary_History", "{0} действий", actions)
            : FormatMany(
                "Agent_Work_Summary",
                "{0} действий · {1}",
                actions,
                FormatElapsed(elapsed));
        var details = FormatMany(
            "Agent_Work_Details",
            "Скрыто шагов: {0}, из них тулов: {1}",
            hidden.Count,
            toolCalls);

        return new AgentChatItemViewModel(AgentChatItemKind.WorkSummary)
        {
            Header = S("Agent_Work", "Работа агента"),
            Text = text,
            Details = details,
            WorkItems = hidden
        };
    }

    private async Task RefreshSessionMetricsAsync(Guid sessionId)
    {
        if (_usageStore is null)
        {
            SessionMetricsText = string.Empty;
            return;
        }

        try
        {
            var metrics = await _usageStore
                .GetSessionMetricsAsync(sessionId, CancellationToken.None)
                .ConfigureAwait(true);
            if (SelectedSession?.Id != sessionId)
                return;

            SessionMetricsText = FormatSessionMetrics(metrics);
        }
        catch (Exception)
        {
            // The analytics line is informational: never break the chat.
            SessionMetricsText = string.Empty;
        }
    }

    private static string FormatSessionMetrics(AgentSessionMetrics? metrics)
    {
        if (metrics is null || metrics.IsEmpty)
            return string.Empty;

        return FormatMany(
            "Agent_Metrics_Session",
            "Сессия: {0} ходов · {1} тулов · LLM {2} · тулы {3} · {4} → {5} токенов · {6}",
            metrics.Turns,
            metrics.ToolCalls,
            FormatElapsed(TimeSpan.FromMilliseconds(metrics.LlmLatencyMs)),
            FormatElapsed(TimeSpan.FromMilliseconds(metrics.ToolDurationMs)),
            FormatCompactSize(metrics.PromptTokens),
            FormatCompactSize(metrics.CompletionTokens),
            FormatCost(metrics.CostUsd));
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
