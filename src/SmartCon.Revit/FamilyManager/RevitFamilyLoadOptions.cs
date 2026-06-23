using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Implementation of IFamilyLoadOptions that delegates the shared-nested-family
/// decision to an optional UI callback. When no callback is supplied, behaves
/// with the legacy defaults (FamilySource.Family + overwriteParameterValues = true).
///
/// Log shape for verification (issue #76 + #77, see ADR-034 §3):
///   <code>
///   [OpId=… Op=FamilyLoadOptions Method=OnFamilyFound FamilyInUse=…] called: overwrite=…
///   [OpId=… Op=FamilyLoadOptions Method=OnSharedFamilyFound Index=1 ApiName=&lt;null&gt; NameSource=CatalogDb FamilyInUse=…]
///     callback available: True
///     Resolve[#1]: CatalogDb fallback — REVIT-198137 TRIGGERED (…), using catalog name='Болт М12' (index 0 of 5)
///     user choice: UseProject
///     applying: Use Project (preserve project version)
///   </code>
///
/// At Debug build level every <c>OnFamilyFound</c> / <c>OnSharedFamilyFound</c>
/// invocation emits one log line per phase so you can confirm:
///   - Issue #76 (R2021): the callbacks ARE invoked (Issue #76 hypothesized
///     that wrapping LoadFamily in a user transaction suppressed them).
///   - Issue #77 (REVIT-198137): when <c>ApiName=&lt;null&gt;</c> the resolver
///     falls back to the catalog-DB name and the dialog still shows the
///     real name to the user.
/// </summary>
public sealed class RevitFamilyLoadOptions : IFamilyLoadOptions
{
    private readonly bool _overwriteParameterValues;
    private readonly Action<string>? _onStatusMessage;
    private readonly Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? _onSharedDecision;
    private readonly SharedFamilyNameResolver _nameResolver;

    public RevitFamilyLoadOptions(
        bool overwriteParameterValues = true,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null)
    {
        _overwriteParameterValues = overwriteParameterValues;
        _onStatusMessage = onStatusMessage;
        _onSharedDecision = onSharedDecision;
        _nameResolver = new SharedFamilyNameResolver(nestedSharedNames);
    }

    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyLoadOptions",
            ("Method", "OnFamilyFound"),
            ("FamilyInUse", familyInUse));
        // Issue #76 verification: this callback fires for the main family
        // being loaded. If LoadFamily is wrapped in a user transaction
        // (REVIT-198137 hypothesis), this callback is suppressed.
        SmartConLogger.Info($"called: overwrite={_overwriteParameterValues}");
        overwriteParameterValues = _overwriteParameterValues;
        return true;
    }

    public bool OnSharedFamilyFound(Autodesk.Revit.DB.Family sharedFamily, bool familyInUse, out Autodesk.Revit.DB.FamilySource source, out bool overwriteParameterValues)
    {
        var invocationIndex = _nameResolver.NextInvocationIndex();
        var apiFamilyName = sharedFamily?.Name;
        var (displayName, nameSource) = _nameResolver.Resolve(apiFamilyName, invocationIndex);
        var logApiName = !string.IsNullOrWhiteSpace(apiFamilyName) ? apiFamilyName : "<null>";
        var revit198137Triggered = string.IsNullOrWhiteSpace(apiFamilyName);

        using var _scope = SmartConLogger.BeginScope("FamilyLoadOptions",
            ("Method", "OnSharedFamilyFound"),
            ("Index", invocationIndex),
            ("ApiName", logApiName),
            ("NameSource", nameSource.ToString()),
            ("FamilyInUse", familyInUse));

        SmartConLogger.Info($"callback available: {_onSharedDecision is not null}");

        // Issue #77 verification: when REVIT-198137 fires the Revit API
        // returns null for `sharedFamily`. We surface the resolved displayName
        // here so an operator can confirm the dialog will show the real
        // name (not "<shared nested #1>") even in Revit 2023 / 2024 < 24.3.0.13.
        SmartConLogger.Info(
            $"Resolved displayName='{displayName}' " +
            $"(REVIT-198137={(revit198137Triggered ? "TRIGGERED" : "not triggered")}, " +
            $"source={nameSource}, catalogCount={_nameResolver.TotalInBatch})");

        if (_onSharedDecision is not null)
        {
            var parentName = ResolveParentFamilyName(sharedFamily);

            var request = new SharedFamilyDecisionRequest(
                SharedFamilyName: displayName,
                IsFamilyInUse: familyInUse,
                ParentFamilyName: parentName,
                IndexInBatch: invocationIndex,
                TotalInBatch: _nameResolver.TotalInBatch,
                NameSource: nameSource);

            SharedFamiliesLoadChoice choice;
            try
            {
                choice = _onSharedDecision(request);
                SmartConLogger.Info($"user choice: {choice}");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"callback threw: {ex.GetType().Name}: {ex.Message} [Action: Falling back to Skip — Revit will not load this shared nested]");
                choice = SharedFamiliesLoadChoice.Skip;
            }

            switch (choice)
            {
                case SharedFamiliesLoadChoice.UseProject:
                    SmartConLogger.Info("applying: Use Project (preserve project version)");
                    source = Autodesk.Revit.DB.FamilySource.Project;
                    overwriteParameterValues = false;
                    _onStatusMessage?.Invoke($"Использовано из проекта: {displayName}");
                    return true;

                case SharedFamiliesLoadChoice.OverwriteParameters:
                    SmartConLogger.Info("applying: Overwrite Parameters");
                    source = Autodesk.Revit.DB.FamilySource.Family;
                    overwriteParameterValues = true;
                    _onStatusMessage?.Invoke($"Обновлено (с параметрами): {displayName}");
                    return true;

                case SharedFamiliesLoadChoice.OverwriteAll:
                    SmartConLogger.Info("applying: Overwrite All");
                    source = Autodesk.Revit.DB.FamilySource.Family;
                    overwriteParameterValues = true;
                    _onStatusMessage?.Invoke($"Полная перезапись: {displayName}");
                    return true;

                case SharedFamiliesLoadChoice.Skip:
                    SmartConLogger.Info("applying: Skip → Use Project (user closed dialog without choosing — preserve project version to avoid aborting parent family load)");
                    source = Autodesk.Revit.DB.FamilySource.Project;
                    overwriteParameterValues = false;
                    _onStatusMessage?.Invoke($"Пропущено (использована версия из проекта): {displayName}");
                    return true;
            }
        }

        SmartConLogger.Info($"default branch: overwrite={_overwriteParameterValues}");
        source = Autodesk.Revit.DB.FamilySource.Family;
        overwriteParameterValues = _overwriteParameterValues;

        if (sharedFamily is not null)
        {
            _onStatusMessage?.Invoke($"Обновлено вложенное семейство: {sharedFamily.Name}");
        }

        return true;
    }

    private static string ResolveParentFamilyName(Autodesk.Revit.DB.Family? sharedFamily)
    {
        if (sharedFamily?.Document is null) return string.Empty;
        try
        {
            return sharedFamily.Document.OwnerFamily?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
