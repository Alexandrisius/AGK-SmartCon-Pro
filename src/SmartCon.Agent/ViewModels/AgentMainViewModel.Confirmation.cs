using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.ViewModels;

/// <summary>
/// Confirmation overlay (mvp-plan §9.2): the tool executor blocks the write
/// until the user presses Apply or Cancel. The overlay shows the operation,
/// the affected count and the raw arguments, and the digest is logged.
/// </summary>
public sealed partial class AgentMainViewModel
{
    private void AttachConfirmationService(AgentConfirmationService service)
    {
        service.Requested += OnConfirmationRequested;
    }

    private void DetachConfirmationService(AgentConfirmationService service)
    {
        service.Requested -= OnConfirmationRequested;
    }

    [ObservableProperty]
    private bool _isConfirmationVisible;

    [ObservableProperty]
    private string _confirmationOperation = string.Empty;

    [ObservableProperty]
    private string _confirmationAffected = string.Empty;

    [ObservableProperty]
    private string _confirmationDetails = string.Empty;

    [ObservableProperty]
    private string _confirmationDigest = string.Empty;

    private void OnConfirmationRequested(AgentConfirmationRequest request)
    {
        var show = new Action(() =>
        {
            ConfirmationOperation = request.ToolName;
            ConfirmationAffected = request.AffectedCount > 0
                ? S("Agent_Confirm_Affected", "Объектов:") + " " + request.AffectedCount
                : string.Empty;
            ConfirmationDetails = FormatConfirmationDetails(request.ArgumentsJson);
            ConfirmationDigest = request.Digest.Length > 12
                ? request.Digest.Substring(0, 12)
                : request.Digest;
            IsConfirmationVisible = true;
        });

        if (_dispatcher.CheckAccess())
            show();
        else
            _ = _dispatcher.InvokeAsync(show);
    }

    [RelayCommand]
    private void ApproveConfirmation()
    {
        IsConfirmationVisible = false;
        _confirmationService.Resolve(AgentConfirmationResult.Approved);
    }

    [RelayCommand]
    private void RejectConfirmation()
    {
        IsConfirmationVisible = false;
        _confirmationService.Resolve(AgentConfirmationResult.Rejected);
    }

    private void HideConfirmation()
    {
        if (!IsConfirmationVisible)
            return;

        IsConfirmationVisible = false;
        _confirmationService.Resolve(AgentConfirmationResult.Cancelled);
    }

    private static string FormatConfirmationDetails(string argumentsJson)
    {
        try
        {
            return Agent.Services.AgentToolResultFormatter.Pretty(argumentsJson);
        }
        catch (Exception)
        {
            return argumentsJson;
        }
    }
}
