using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed class RevitFamilyLoadService : IFamilyLoadService
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _transactionService;

    public RevitFamilyLoadService(IRevitContext revitContext, ITransactionService transactionService)
    {
        _revitContext = revitContext;
        _transactionService = transactionService;
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

            var preferredName = options.PreferredName?.Trim();
            if (!string.IsNullOrWhiteSpace(preferredName)
                && !string.Equals(family.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    family.Name = preferredName;
                    renameResult = $"Renamed family to '{preferredName}'";
                }
                catch (Exception ex)
                {
                    renameResult = $"Rename failed (non-fatal): {ex.Message}";
                }
            }
        });

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

    public Task<FamilyLoadResult> LoadFamilyAsync(FamilyResolvedFile file, FamilyLoadOptions options, Action<string>? onStatusMessage = null, Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null, CancellationToken ct = default)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return Task.FromResult(new FamilyLoadResult(false, null, null, "No active document", FamilyLoadStatus.Failed));

        var normalizedPath = Path.GetFullPath(file.AbsolutePath);
        var fileName = Path.GetFileName(normalizedPath);
        using var _scope = SmartConLogger.BeginScope("FamilyLoad",
            ("Method", "LoadFamilyAsync"),
            ("FilePath", fileName));

        SmartConLogger.Info("Attempting to load family");
        SmartConLogger.Info($"File exists: {File.Exists(normalizedPath)}");
        SmartConLogger.Info($"Current Revit version: {doc.Application.VersionNumber}");

        if (!File.Exists(normalizedPath))
        {
            SmartConLogger.Info("File not found");
            return Task.FromResult(new FamilyLoadResult(false, null, null, $"File not found: {normalizedPath}", FamilyLoadStatus.Failed));
        }

        try
        {
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
                    return Task.FromResult(new FamilyLoadResult(false, null, null,
                        $"Family was saved in Revit {fileFormat} and cannot be opened in the current version.", FamilyLoadStatus.Failed));
                }

                if (!isCurrentVersion)
                {
                    SmartConLogger.Info($"UPGRADE DIALOG EXPECTED (version {fileFormat})");
                }
            }

            var preferredName = options.PreferredName?.Trim();
            var checkName = !string.IsNullOrWhiteSpace(preferredName)
                ? preferredName
                : SafeFileName.GetBaseName(normalizedPath).Trim();

            SmartConLogger.Info($"Checking for existing family by name: '{checkName}'");

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

            var loadOptions = new RevitFamilyLoadOptions(options.OverwriteParameterValues, onStatusMessage, onSharedDecision);

            SmartConLogger.Info("Attempt 1: LoadFamily with options in transaction...");
            var result1 = TryLoadInTransaction(doc, normalizedPath, loadOptions, options, "Attempt1", existingFamily);
            if (result1 is not null)
                return Task.FromResult(result1);
            SmartConLogger.Info("Attempt 1 failed (returned null)");

            SmartConLogger.Info("Attempt 2: LoadFamily without IFamilyLoadOptions...");
            var result2 = TryLoadInTransaction(doc, normalizedPath, null, options, "Attempt2", existingFamily);
            if (result2 is not null)
                return Task.FromResult(result2);
            SmartConLogger.Info("Attempt 2 failed (returned null)");

            var errorMessage = BuildErrorMessage(normalizedPath);
            SmartConLogger.Info($"Both attempts failed - returning error: {errorMessage}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, errorMessage, FamilyLoadStatus.Failed));
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"LoadFamily exception: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, ex.Message, FamilyLoadStatus.Failed));
        }
    }

    public Task<FamilyLoadResult> LoadFamilySymbolAsync(string filePath, string typeName, Action<string>? onStatusMessage = null, Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null, CancellationToken ct = default)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return Task.FromResult(new FamilyLoadResult(false, null, null, "No active document", FamilyLoadStatus.Failed));

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
            return Task.FromResult(new FamilyLoadResult(false, null, null, $"File not found: {normalizedPath}", FamilyLoadStatus.Failed));
        }

        try
        {
            var loadOptions = new RevitFamilyLoadOptions(overwriteParameterValues: true, onStatusMessage, onSharedDecision);
            bool loaded = false;
            Autodesk.Revit.DB.FamilySymbol? symbol = null;

            _transactionService.RunInTransaction("Load Family Symbol", _ =>
            {
                loaded = doc.LoadFamilySymbol(normalizedPath, typeName, loadOptions, out symbol);
            });

            if (loaded && symbol is not null)
            {
                var familyName = symbol.FamilyName;
                SmartConLogger.Info($"Symbol '{typeName}' loaded successfully from family '{familyName}'");
                return Task.FromResult(new FamilyLoadResult(true, familyName, $"Type '{typeName}' loaded", null, FamilyLoadStatus.Loaded));
            }

            // If LoadFamilySymbol returns false, it may be because the type already exists.
            // Try to find the symbol in the existing family.
            var existingFamily = FindExistingFamily(doc, SafeFileName.GetBaseName(normalizedPath));
            if (existingFamily is null)
            {
                existingFamily = FindExistingFamily(doc, typeName);
            }

            if (existingFamily is not null)
            {
                var existingSymbol = existingFamily.GetFamilySymbolIds()
                    .Select(id => doc.GetElement(id))
                    .OfType<Autodesk.Revit.DB.FamilySymbol>()
                    .FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

                if (existingSymbol is not null)
                {
                    SmartConLogger.Info($"Symbol '{typeName}' already exists in family '{existingFamily.Name}'");
                    return Task.FromResult(new FamilyLoadResult(true, existingFamily.Name, $"Type '{typeName}' already exists", null, FamilyLoadStatus.Current));
                }
            }

            SmartConLogger.Info($"Failed to load symbol '{typeName}'");
            return Task.FromResult(new FamilyLoadResult(false, null, null, $"Failed to load type '{typeName}'", FamilyLoadStatus.Failed));
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"Exception: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, ex.Message, FamilyLoadStatus.Failed));
        }
    }
}
