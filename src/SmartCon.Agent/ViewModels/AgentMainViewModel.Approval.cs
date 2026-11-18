using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.ViewModels;

/// <summary>
/// C1 UI: the approval mode is a session-scoped safety switch. The default is
/// full confirmation; auto-approve and read-only are explicit, visible choices
/// and are reset when the active document changes.
/// </summary>
public sealed partial class AgentMainViewModel
{
    private AgentSessionContext? _sessionContext;

    public IReadOnlyList<AgentApprovalModeOption> ApprovalModes { get; } = new[]
    {
        new AgentApprovalModeOption(
            AgentApprovalMode.ConfirmWrites,
            S("Agent_Approval_Confirm", "Подтверждение")),
        new AgentApprovalModeOption(
            AgentApprovalMode.AutoApproveSession,
            S("Agent_Approval_Auto", "Автоприменение")),
        new AgentApprovalModeOption(
            AgentApprovalMode.ReadOnly,
            S("Agent_Approval_ReadOnly", "Только чтение"))
    };

    [ObservableProperty]
    private AgentApprovalModeOption? _selectedApprovalMode;

    public bool IsApprovalWarningVisible =>
        _sessionContext is not null
        && _sessionContext.ApprovalMode != AgentApprovalMode.ConfirmWrites;

    public string ApprovalWarningText =>
        _sessionContext?.ApprovalMode == AgentApprovalMode.ReadOnly
            ? S(
                "Agent_Approval_ReadOnlyBanner",
                "Режим «Только чтение»: агент не может изменять модель.")
            : S(
                "Agent_Approval_AutoBanner",
                "Автоприменение включено: изменения применяются без подтверждения. " +
                "Undo доступен для каждого набора.");

    public string ApprovalModeTooltip => S(
        "Agent_Approval_Tooltip",
        "Режим подтверждения изменений. Сбрасывается при смене документа.");

    public string ApprovalDisableText => S("Agent_Approval_Disable", "Выключить");

    internal void AttachSessionContext(AgentSessionContext sessionContext)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        RefreshApprovalMode();
    }

    internal void ResetApprovalModeForDocumentChange()
    {
        if (_sessionContext is null)
            return;

        _sessionContext.ApprovalMode = AgentApprovalMode.ConfirmWrites;
        RefreshApprovalMode();
    }

    internal void RefreshApprovalMode()
    {
        var current = _sessionContext?.ApprovalMode ?? AgentApprovalMode.ConfirmWrites;
        SelectedApprovalMode = ApprovalModes.First(option => option.Mode == current);
        OnPropertyChanged(nameof(IsApprovalWarningVisible));
        OnPropertyChanged(nameof(ApprovalWarningText));
    }

    [RelayCommand]
    private void DisableApprovalBypass() => SelectedApprovalMode = ApprovalModes[0];

    partial void OnSelectedApprovalModeChanged(AgentApprovalModeOption? value)
    {
        if (_sessionContext is null || value is null
            || _sessionContext.ApprovalMode == value.Mode)
        {
            return;
        }

        _sessionContext.ApprovalMode = value.Mode;
        OnPropertyChanged(nameof(IsApprovalWarningVisible));
        OnPropertyChanged(nameof(ApprovalWarningText));
    }
}

public sealed record AgentApprovalModeOption(AgentApprovalMode Mode, string Label);
