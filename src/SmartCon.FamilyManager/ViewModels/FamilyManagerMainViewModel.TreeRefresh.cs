using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Common;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Selectors;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    internal static CatalogTreeNodeViewModel? FindParentOf(ObservableCollection<CatalogTreeNodeViewModel> nodes, CatalogTreeNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(target)) return node;
            var found = FindParentOf(node.Children, target);
            if (found is not null) return found;
        }
        return null;
    }

    internal static int CountFamiliesRecursive(CatalogTreeNodeViewModel node)
    {
        var count = 0;
        foreach (var child in node.Children)
        {
            if (child is FamilyLeafNodeViewModel)
                count++;
            else
                count += CountFamiliesRecursive(child);
        }
        return count;
    }

    internal static void CollectExpandedIds(ObservableCollection<CatalogTreeNodeViewModel>? nodes, HashSet<string> catIds, HashSet<string>? familyIds)
    {
        if (nodes is null) return;
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
            {
                if (node is CategoryNodeViewModel cat) catIds.Add(cat.CategoryId);
                else if (node is FamilyLeafNodeViewModel leaf && familyIds is not null) familyIds.Add(leaf.CatalogItemId);
            }
            CollectExpandedIds(node.Children, catIds, familyIds);
        }
    }

    internal static void CollectFamilyIds(ObservableCollection<CatalogTreeNodeViewModel> nodes, List<string> ids)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyLeafNodeViewModel leaf)
                ids.Add(leaf.CatalogItemId);
            CollectFamilyIds(node.Children, ids);
        }
    }

    internal static void AttachTypesToNodes(ObservableCollection<CatalogTreeNodeViewModel> nodes, IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>> batch, HashSet<string> expandedFamilyIds)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyLeafNodeViewModel leaf)
            {
                if (batch.TryGetValue(leaf.CatalogItemId, out var types))
                {
                    foreach (var t in types)
                    {
                        // #172: the synthetic <default> row exists in family_types
                        // only for content-hash and attribute stability (ADR-049/056).
                        // In the tree it must behave like the v2.0.0 virtual node
                        // (family name, IsVirtual) — otherwise Place/DnD would send
                        // the raw "<default>" literal to LoadFamilySymbol and fail,
                        // and the user would see the marker instead of the family name.
                        if (t.Name == FamilyTypeSnapshot.DefaultTypeName)
                        {
                            AddVirtualTypeNode(leaf);
                            continue;
                        }

                        leaf.Children.Add(new FamilyTypeNodeViewModel(
                            t.CatalogItemId, t.Name, isVirtual: false,
                            familySource: leaf.FamilySource, uniqueId: t.UniqueId,
                            displayName: FamilyTypeSnapshot.ResolveDisplayName(t.Name, leaf.DisplayName),
                            isUnavailable: leaf.IsUnavailable,
                            familyName: t.FamilyName,
                            familyKey: t.FamilyKey));
                    }
                }

                if (leaf.Children.Count == 0)
                {
                    AddVirtualTypeNode(leaf);
                }

                if (expandedFamilyIds.Contains(leaf.CatalogItemId))
                    leaf.IsExpanded = true;
            }
            AttachTypesToNodes(node.Children, batch, expandedFamilyIds);
        }
    }

    private static void AddVirtualTypeNode(FamilyLeafNodeViewModel leaf)
    {
        leaf.Children.Add(new FamilyTypeNodeViewModel(
            leaf.CatalogItemId, leaf.DisplayName, isVirtual: true,
            familySource: leaf.FamilySource, isUnavailable: leaf.IsUnavailable));
    }

    /// <summary>
    /// Triggers tree refresh via ExternalEvent. The RaiseAsync round-trip is
    /// LOAD-BEARING, not a warm-up (regression 2026-08-13): the first action
    /// processed by the AwaitableEvent wires <see cref="IRevitContext"/>
    /// (<c>ProcessQueue → IRevitContextWriter.SetContext</c> — Revit version,
    /// username). <c>RefreshAccessAndLoadTreeAsync</c> below calls
    /// <c>DetectRevitVersion()</c>, so the round-trip MUST complete before the
    /// dispatcher runs it — without it <c>CurrentRevitVersion</c> stays 0 for
    /// the whole session and every version-gated operation
    /// (<c>ResolveForLoadAsync</c>: «Загрузить в проект», «Редактировать»)
    /// fails with "No compatible version found".
    /// </summary>
    private async Task RefreshTreeViaExternalEventAsync()
    {
        try
        {
            await _awaitableEvent.RaiseAsync(_ => { }).ConfigureAwait(true);

            IsLoading = true;
            try
            {
                // v2.0.0 (ADR-036): IDispatcher.InvokeAsync takes an Action, but
                // RefreshTreeOnUiThreadAsync returns Task. Wrapping the async
                // lambda directly would generate an async void state machine,
                // which violates I-13 (exception swallowing) and breaks
                // testability. Extracting the async work into a named Task-
                // returning method and dispatching a fire-and-forget Action
                // (via `_ =`) keeps the Action synchronous and the exception
                // path observable through RefreshTreeOnUiThreadAsync itself.
                _ = _dispatcher.InvokeAsync(() => { _ = RefreshTreeOnUiThreadAsync(); });
            }
            finally
            {
                IsLoading = false;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RefreshTreeViaExternalEvent failed: {ex.Message} [Action: click Refresh to retry, check smartcon.log]");
        }
    }

    private async Task RefreshTreeOnUiThreadAsync()
    {
        try
        {
            await RefreshAccessAndLoadTreeAsync();
        }
        catch (DbAccessDeniedException ex)
        {
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            IsEditorRole = false;
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied",
                string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AccessDeniedMessage) ?? "The owner of \"{0}\" has restricted your access.", ex.DbName));
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RefreshTreeAsync failed: {ex.Message} [Action: click Refresh to retry, check smartcon.log]");
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ErrorFormat) ?? "Error: {0}",
                ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshTree()
    {
        await RefreshTreeViaExternalEventAsync();
    }
}
