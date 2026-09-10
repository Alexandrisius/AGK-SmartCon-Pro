using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
    /// <summary>
    /// Saves the routing edits of every touched type in place (item-level
    /// links, no version is created). Called from the main <c>SaveAsync</c>
    /// BEFORE the metadata save; a failure aborts the whole save (the error
    /// is already shown).
    /// </summary>
    private async Task<bool> SaveRoutingAsync()
    {
        if (_routingEditorService is null)
            return false;

        var editedTypes = new List<RoutingEditorTypeSave>();
        foreach (var type in _routingData!.Types)
        {
            var key = RoutingTypeItem.KeyOf(type.TypeName, type.FamilyKey);
            var state = _routingEdits[key];
            if (string.Equals(_routingOriginals[key], state.Fingerprint(), StringComparison.Ordinal))
                continue;
            if (!state.TryToRecords(type, out var records, out var invalidGroup))
            {
                _dialogService.ShowError(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Tab_Routing) ?? "Routing",
                    string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_InvalidSize) ?? "{0} ({1})",
                        invalidGroup, type.TypeName));
                return false;
            }
            editedTypes.Add(new RoutingEditorTypeSave(
                type.TypeName, type.FamilyKey, state.PreferredJunctionType, records));
        }

        if (editedTypes.Count == 0)
            return true;

        var result = await _routingEditorService
            .SaveAsync(_catalogItemId, new RoutingEditorSave(editedTypes))
            .ConfigureAwait(true);
        if (!result.Success)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_Tab_Routing) ?? "Routing",
                string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Routing_SaveFailed) ?? "{0}",
                    result.ErrorMessage));
            return false;
        }

        // A no-op save (phantom rows only) must NOT trigger the stale
        // re-check downstream — nothing in the catalog actually changed.
        RoutingLinksChanged |= result.Changed;

        var status = LanguageManager.GetString(StringLocalization.Keys.FM_Routing_Saved) ?? "Saved";
        if (result.ArchivedLockedParts.Count > 0)
        {
            status += Environment.NewLine + string.Join(
                Environment.NewLine,
                result.ArchivedLockedParts.Select(part => string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Routing_ArchivedLockHint) ?? "{0}",
                    part)));
        }
        RoutingStatusMessage = status;

        // Reload from the DB so the editor state matches what was persisted
        // (and the dirty baseline resets). The reload resets the type
        // selector to the first type — restore the user's context (audit L4).
        var selectedKey = SelectedRoutingType is { } selected
            ? RoutingTypeItem.KeyOf(selected.TypeName, selected.FamilyKey)
            : null;
        await LoadRoutingAsync(default).ConfigureAwait(true);
        if (selectedKey is not null)
        {
            SelectedRoutingType = RoutingTypes.FirstOrDefault(t =>
                RoutingTypeItem.KeyOf(t.TypeName, t.FamilyKey) == selectedKey)
                ?? RoutingTypes.FirstOrDefault();
        }
        return true;
    }
}
