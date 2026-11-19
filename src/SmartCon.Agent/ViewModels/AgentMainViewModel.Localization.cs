using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.ViewModels;

/// <summary>
/// Runtime language switch: the panel is a persistent dockable pane, so labels
/// created in the view model must be re-localized instead of staying in the
/// language that was active when the view model was constructed.
/// </summary>
public sealed partial class AgentMainViewModel
{
    private void OnLanguageChanged()
    {
        if (_dispatcher.CheckAccess())
            RefreshLocalization();
        else
            _dispatcher.Invoke(RefreshLocalization);
    }

    private void RefreshLocalization()
    {
        foreach (var option in ApprovalModes)
            option.RefreshLabel();

        OnPropertyChanged(nameof(ApprovalWarningText));
        OnPropertyChanged(nameof(ApprovalModeTooltip));
        OnPropertyChanged(nameof(ApprovalDisableText));
        OnPropertyChanged(nameof(PrimaryActionTooltip));
        UpdateBanner();

        foreach (var item in Messages)
        {
            switch (item.Kind)
            {
                case AgentChatItemKind.User:
                    item.Header = S("Agent_User", "Вы");
                    break;
                case AgentChatItemKind.Assistant:
                    item.Header = S("Agent_Assistant", "BIM Agent");
                    break;
                case AgentChatItemKind.Tool when !string.IsNullOrWhiteSpace(item.ToolName):
                    item.Header = AgentToolDisplayNames.Get(item.ToolName);
                    break;
                case AgentChatItemKind.Error:
                    item.Header = S("Agent_Error", "Ошибка");
                    break;
            }
        }
    }
}