using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Implementation of <see cref="IFamilyLoadService"/> for Revit.
///
/// Log shape at Debug build level (verifies both bugs from the GitHub issues):
///   - Issue #76 (R2021): the callback flow is observed end-to-end:
///     <c>OnFamilyFound</c> fires once for the main family, <c>OnSharedFamilyFound</c>
///     fires once per conflicting shared nested. If the dialog is wrapped in a
///     user transaction (hypothesised cause of #76), neither callback fires
///     and the family is silently overwritten.
///   - Issue #77 (REVIT-198137): when <c>ApiName=&lt;null&gt;</c> in the
///     <c>OnSharedFamilyFound</c> scope, the resolver falls back to the
///     catalog-DB name and the dialog shows the real name instead of the
///     Revit native conflict UI.
/// </summary>
public sealed partial class RevitFamilyLoadService : IFamilyLoadService, IFamilyLoadServiceSourceAware
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _transactionService;
    private readonly ISharedNestedFamilyRepository? _nestedSharedRepository;

    public RevitFamilyLoadService(
        IRevitContext revitContext,
        ITransactionService transactionService,
        ISharedNestedFamilyRepository? nestedSharedRepository = null)
    {
        _revitContext = revitContext;
        _transactionService = transactionService;
        _nestedSharedRepository = nestedSharedRepository;
    }

    private static Autodesk.Revit.DB.Family? FindExistingFamily(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private FamilyLoadResult? TryLoadInTransaction(
        Document doc, string path, RevitFamilyLoadOptions? loadOptions, FamilyLoadOptions options, string attemptName, Autodesk.Revit.DB.Family? existingFamily)
    {
        Autodesk.Revit.DB.Family? loadedFamily = null;
        bool success = false;

        string? renameResult = null;
        // Logging is captured into locals inside the transaction lambda and
        // emitted AFTER it commits — file I/O on the main thread inside a
        // transaction callback is a known WPF freeze factor
        // (revit-api-best-practice/references/transaction-callback-freeze.md).
        string? loadedDescription = null;

        // Issue #76 verification: log the attempt number and whether
        // IFamilyLoadOptions is supplied (so OnSharedFamilyFound callback
        // will fire). At Debug level this is visible per attempt.
        if (loadOptions is not null)
        {
            SmartConLogger.Info(
                $"[{attemptName}] LoadFamily WITH IFamilyLoadOptions — " +
                "expecting OnFamilyFound + OnSharedFamilyFound callbacks (Issue #76 verification)");
        }
        else
        {
            SmartConLogger.Info(
                $"[{attemptName}] LoadFamily WITHOUT IFamilyLoadOptions — " +
                "no callbacks will fire (silent overwrite fallback path)");
        }

        _transactionService.RunInTransaction("Load Family", _ =>
        {
            bool loaded;
            Autodesk.Revit.DB.Family family;
            if (loadOptions is not null)
                loaded = doc.LoadFamily(path, loadOptions, out family);
            else
                loaded = doc.LoadFamily(path, out family);

            if (!loaded || family is null)
                return;

            loadedFamily = family;
            success = true;
            loadedDescription = RevitFamilySearchService.DescribeFamily(family);

            var preferredName = options.PreferredName?.Trim();
            if (!string.IsNullOrWhiteSpace(preferredName)
                && !string.Equals(family.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var nameBeforeRename = family.Name;
                    family.Name = preferredName;
                    renameResult = $"Renamed family '{nameBeforeRename}' to '{preferredName}'";
                }
                catch (Exception ex)
                {
                    renameResult = $"Rename failed (non-fatal): {ex.Message}";
                }
            }
        });

        if (loadedDescription is not null)
            SmartConLogger.Info($"[{attemptName}] LoadFamily returned: {loadedDescription}, sourcePath='{path}'");

        if (renameResult is not null)
            SmartConLogger.Info($"[{attemptName}] {renameResult}");

        if (success && loadedFamily is not null)
        {
            var displayName = loadedFamily.Name;
            var status = existingFamily is null
                ? FamilyLoadStatus.Loaded
                : FamilyLoadStatus.Updated;
            var msg = status == FamilyLoadStatus.Updated
                ? $"Family '{displayName}' updated to latest version"
                : $"Family '{displayName}' loaded successfully";
            SmartConLogger.Info($"[{attemptName}] {msg}");
            return new FamilyLoadResult(true, displayName, msg, null, status);
        }

        if (!success && loadedFamily is null && existingFamily is not null)
        {
            // Revit rejected the load because the family is already up-to-date.
#if REVIT2021_OR_GREATER
            SmartConLogger.Info($"[{attemptName}] Family '{existingFamily.Name}' is already current (VersionGuid unchanged)");
#else
            SmartConLogger.Info($"[{attemptName}] Family '{existingFamily.Name}' is already current");
#endif
            return new FamilyLoadResult(true, existingFamily.Name,
                $"Family '{existingFamily.Name}' is already up-to-date", null, FamilyLoadStatus.Current);
        }

        SmartConLogger.Info($"[{attemptName}] Failed: loadedFamily is null or success=false");
        return null;
    }

    private static string BuildErrorMessage(string path)
    {
        var fileName = Path.GetFileName(path);
        var nameWithoutExt = SafeFileName.GetBaseName(path);

        if (nameWithoutExt.Length > 0 && nameWithoutExt[nameWithoutExt.Length - 1] == ' ')
        {
            return $"File name has a trailing space before extension: '{fileName}'. Rename the file and re-import.";
        }

        if (path.Length > 240)
        {
            return $"File path is too long ({path.Length} chars). Move the file to a shorter path.";
        }

        return "Unable to load family. The file may be from a newer Revit version or incompatible with this project.";
    }

    public async Task<FamilyLoadResult> LoadFamilyAsync(
        FamilyResolvedFile file,
        FamilyLoadOptions options,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return new FamilyLoadResult(false, null, null, "No active document", FamilyLoadStatus.Failed);

        var normalizedPath = Path.GetFullPath(file.AbsolutePath);
        var fileName = Path.GetFileName(normalizedPath);
        // C15: scope wraps only the preparation phase (file info + DB lookup).
        // NOT the actual LoadFamily call, which can block for minutes on
        // a user-blocking WPF dialog (OnSharedFamilyFound). Each attempt
        // (Attempt 1 / Attempt 2) gets its own inner scope inside
        // TryLoadInTransaction. Correlation across attempts is via the
        // FilePath scope property.
        var resolvedNestedNames = await PrepareForLoadAsync(file, fileName, normalizedPath, options, nestedSharedNames, ct).ConfigureAwait(true);

        if (!File.Exists(normalizedPath))
        {
            return new FamilyLoadResult(false, null, null, $"File not found: {normalizedPath}", FamilyLoadStatus.Failed);
        }

        try
        {
            var checkName = !string.IsNullOrWhiteSpace(options.PreferredName?.Trim())
                ? options.PreferredName!.Trim()
                : SafeFileName.GetBaseName(normalizedPath).Trim();

            var existingFamily = FindExistingFamily(doc, checkName!);
            if (existingFamily is not null)
            {
#if REVIT2021_OR_GREATER
                SmartConLogger.Info($"Family '{checkName}' found in project (Id={existingFamily.Id}, VersionGuid={existingFamily.VersionGuid})");
#else
                SmartConLogger.Info($"Family '{checkName}' found in project (Id={existingFamily.Id})");
#endif
            }
            else
            {
                SmartConLogger.Info($"No existing family found with name '{checkName}'");
            }

            // Issue #77 verification (Debug): log the fallback names count so
            // an operator can confirm the catalog has data ready for
            // REVIT-198137 fallback (when ApiName is null in OnSharedFamilyFound).
            var fallbackSummary = resolvedNestedNames is { Count: > 0 }
                ? $"ready (REVIT-198137 fallback will use catalog if ApiName is null)"
                : "empty (dialog may show placeholder in Revit 2023/2024.2)";
            SmartConLogger.Info(
                $"Catalog fallback names for this load: {fallbackSummary} " +
                $"(Count={(resolvedNestedNames?.Count ?? 0)})");

            var loadOptions = new RevitFamilyLoadOptions(
                options.OverwriteParameterValues, onStatusMessage, onSharedDecision, resolvedNestedNames);

            SmartConLogger.Info("Attempt 1: LoadFamily with options in transaction...");
            var result1 = TryLoadInTransaction(doc, normalizedPath, loadOptions, options, "Attempt1", existingFamily);
            if (result1 is not null)
                return result1;
            SmartConLogger.Info("Attempt 1 failed (returned null)");

            SmartConLogger.Info("Attempt 2: LoadFamily without IFamilyLoadOptions...");
            var result2 = TryLoadInTransaction(doc, normalizedPath, null, options, "Attempt2", existingFamily);
            if (result2 is not null)
                return result2;
            SmartConLogger.Info("Attempt 2 failed (returned null)");

            var errorMessage = BuildErrorMessage(normalizedPath);
            SmartConLogger.Info($"Both attempts failed - returning error: {errorMessage}");
            return new FamilyLoadResult(false, null, null, errorMessage, FamilyLoadStatus.Failed);
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"LoadFamily exception: {ex.GetType().Name}: {ex.Message}");
            return new FamilyLoadResult(false, null, null, ex.Message, FamilyLoadStatus.Failed);
        }
    }

    private async Task<IReadOnlyList<string>?> PrepareForLoadAsync(
        FamilyResolvedFile file,
        string fileName,
        string normalizedPath,
        FamilyLoadOptions options,
        IReadOnlyList<string>? nestedSharedNames,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyLoad",
            ("Method", "LoadFamilyAsync"),
            ("FilePath", fileName));

        SmartConLogger.Info("Attempting to load family");
        SmartConLogger.Info($"File exists: {File.Exists(normalizedPath)}");

        if (!File.Exists(normalizedPath))
        {
            SmartConLogger.Info("File not found");
            return null;
        }

        var fileInfo = new FileInfo(normalizedPath);
        SmartConLogger.Info($"File size: {fileInfo.Length} bytes");

        using var basicInfo = BasicFileInfo.Extract(normalizedPath);
        if (basicInfo != null)
        {
            var fileFormat = basicInfo.Format ?? "unknown";
            var isCurrentVersion = basicInfo.IsSavedInCurrentVersion;
            SmartConLogger.Info($"File version={fileFormat}, IsCurrentVersion={isCurrentVersion}, LaterVersion={basicInfo.IsSavedInLaterVersion}");

            if (basicInfo.IsSavedInLaterVersion)
            {
                SmartConLogger.Info($"Family saved in newer version: {fileFormat}");
                return null;
            }

            if (!isCurrentVersion)
            {
                SmartConLogger.Info($"UPGRADE DIALOG EXPECTED (version {fileFormat})");
            }
        }

        return await ResolveNestedNamesAsync(file, nestedSharedNames, ct).ConfigureAwait(true);
    }

    public async Task<FamilyLoadResult> LoadFamilySymbolAsync(
        string filePath,
        string typeName,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        string? catalogItemId = null,
        CancellationToken ct = default)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return new FamilyLoadResult(false, null, null, "No active document", FamilyLoadStatus.Failed);

        var normalizedPath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(normalizedPath);
        using var _scope = SmartConLogger.BeginScope("FamilyLoadSymbol",
            ("Method", "LoadFamilySymbolAsync"),
            ("TypeName", typeName),
            ("FilePath", fileName));

        SmartConLogger.Info($"Attempting to load symbol '{typeName}'");

        if (!File.Exists(normalizedPath))
        {
            SmartConLogger.Info("File not found");
            return new FamilyLoadResult(false, null, null, $"File not found: {normalizedPath}", FamilyLoadStatus.Failed);
        }

        try
        {
            // Resolve nested names: caller-provided take priority; otherwise
            // look up the catalog DB by CatalogItemId. The Type Catalog load
            // path is the one most likely to trigger REVIT-198137 (every
            // type re-resolves the parent family and triggers
            // OnSharedFamilyFound), so this lookup is important for
            // Revit 2023 / 2024 < 24.3.0.13.
            IReadOnlyList<string>? resolvedNestedNames = nestedSharedNames;
            if (resolvedNestedNames is null
                && !string.IsNullOrEmpty(catalogItemId)
                && _nestedSharedRepository is not null)
            {
                try
                {
                    resolvedNestedNames = await _nestedSharedRepository
                        .GetNamesForCurrentVersionAsync(catalogItemId!, ct)
                        .ConfigureAwait(true);
                    if (resolvedNestedNames is { Count: > 0 })
                    {
                        SmartConLogger.Info(
                            $"Loaded {resolvedNestedNames.Count} nested names from catalog DB for '{catalogItemId}' " +
                            "— will use as fallback if Revit API returns null (REVIT-198137)");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to read nested names from catalog DB for symbol load: " +
                        $"{ex.GetType().Name}: {ex.Message} " +
                        "[Action: continuing without fallback names — dialog may show placeholder in Revit 2023/2024.2]");
                }
            }
            else if (resolvedNestedNames is null && string.IsNullOrEmpty(catalogItemId))
            {
                SmartConLogger.Warn(
                    "LoadFamilySymbolAsync called without catalogItemId and without explicit nestedSharedNames " +
                    "[Action: caller should pass catalogItemId to enable REVIT-198137 fallback for symbol-based loads]");
            }

            // Issue #77 verification (Debug): same summary as LoadFamilyAsync.
            var fallbackSummary = resolvedNestedNames is { Count: > 0 }
                ? "ready (REVIT-198137 fallback will use catalog if ApiName is null)"
                : "empty (dialog may show placeholder in Revit 2023/2024.2)";
            SmartConLogger.Info(
                $"Catalog fallback names for this symbol load: {fallbackSummary} " +
                $"(Count={(resolvedNestedNames?.Count ?? 0)})");

            var loadOptions = new RevitFamilyLoadOptions(
                overwriteParameterValues: true, onStatusMessage, onSharedDecision, resolvedNestedNames);
            bool loaded = false;
            Autodesk.Revit.DB.FamilySymbol? symbol = null;

            _transactionService.RunInTransaction("Load Family Symbol", _ =>
            {
                loaded = doc.LoadFamilySymbol(normalizedPath, typeName, loadOptions, out symbol);
            });

            if (loaded && symbol is not null)
            {
                var familyName = symbol.FamilyName;
                var hostFamily = symbol.Family;
                SmartConLogger.Info(
                    $"Symbol '{typeName}' loaded successfully from family '{familyName}' — " +
                    $"hostFamily=({RevitFamilySearchService.DescribeFamily(hostFamily)}), " +
                    $"symbolUniqueId={symbol.UniqueId}, sourcePath='{normalizedPath}'");
                return new FamilyLoadResult(true, familyName, $"Type '{typeName}' loaded", null, FamilyLoadStatus.Loaded);
            }

            SmartConLogger.Info(
                $"LoadFamilySymbol returned false for '{typeName}' from '{normalizedPath}' — " +
                "falling back to existing family lookup");

            // If LoadFamilySymbol returns false, it may be because the type already exists.
            // Try to find the symbol in the existing family.
            var existingFamily = FindExistingFamily(doc, SafeFileName.GetBaseName(normalizedPath));
            if (existingFamily is null)
            {
                existingFamily = FindExistingFamily(doc, typeName);
            }

            if (existingFamily is not null)
            {
                SmartConLogger.Info(
                    $"Fallback found existing family: {RevitFamilySearchService.DescribeFamily(existingFamily)}");

                var existingSymbol = existingFamily.GetFamilySymbolIds()
                    .Select(id => doc.GetElement(id))
                    .OfType<Autodesk.Revit.DB.FamilySymbol>()
                    .FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

                if (existingSymbol is not null)
                {
                    SmartConLogger.Info($"Symbol '{typeName}' already exists in family '{existingFamily.Name}'");
                    return new FamilyLoadResult(true, existingFamily.Name, $"Type '{typeName}' already exists", null, FamilyLoadStatus.Current);
                }
            }

            SmartConLogger.Info($"Failed to load symbol '{typeName}'");
            return new FamilyLoadResult(false, null, null, $"Failed to load type '{typeName}'", FamilyLoadStatus.Failed);
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"Exception: {ex.GetType().Name}: {ex.Message}");
            return new FamilyLoadResult(false, null, null, ex.Message, FamilyLoadStatus.Failed);
        }
    }

    private async Task<IReadOnlyList<string>?> ResolveNestedNamesAsync(
        FamilyResolvedFile file,
        IReadOnlyList<string>? callerProvided,
        CancellationToken ct)
    {
        if (callerProvided is not null) return callerProvided;
        if (_nestedSharedRepository is null) return null;
        var catalogItemId = file.CatalogItemId;
        if (string.IsNullOrEmpty(catalogItemId))
        {
            SmartConLogger.Debug("FamilyResolvedFile.CatalogItemId is empty — no catalog lookup");
            return null;
        }
        try
        {
            var names = await _nestedSharedRepository
                .GetNamesForCurrentVersionAsync(catalogItemId!, ct)
                .ConfigureAwait(true);
            if (names.Count > 0)
            {
                SmartConLogger.Info(
                    $"Loaded {names.Count} nested names from catalog DB for '{catalogItemId}' " +
                    "— will use as fallback if Revit API returns null (REVIT-198137)");
            }
            else
            {
                SmartConLogger.Debug(
                    $"Catalog lookup for '{catalogItemId}': empty list (legacy catalog or no shared nested in this family)");
            }
            return names;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to read nested names from catalog DB: {ex.GetType().Name}: {ex.Message} " +
                "[Action: continuing without fallback names — dialog may show placeholder in Revit 2023/2024.2]");
            return null;
        }
    }
}
