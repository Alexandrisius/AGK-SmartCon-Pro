using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
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
                    renameResult = $"[FamilyLoad] Renamed family to '{preferredName}'";
                }
                catch (Exception ex)
                {
                    renameResult = $"[FamilyLoad] Rename failed (non-fatal): {ex.Message}";
                }
            }
        });

        if (renameResult is not null)
            SmartConLogger.Info(renameResult);

        if (success && loadedFamily is not null)
        {
            var displayName = loadedFamily.Name;
            var status = existingFamily is null
                ? FamilyLoadStatus.Loaded
                : FamilyLoadStatus.Updated;
            var msg = status == FamilyLoadStatus.Updated
                ? $"Family '{displayName}' updated to latest version"
                : $"Family '{displayName}' loaded successfully";
            SmartConLogger.Info($"[FamilyLoad][{attemptName}] {msg}");
            return new FamilyLoadResult(true, displayName, msg, null, status);
        }

        if (!success && loadedFamily is null && existingFamily is not null)
        {
            // Revit rejected the load because the family is already up-to-date.
#if REVIT2021_OR_GREATER
            SmartConLogger.Info($"[FamilyLoad][{attemptName}] Family '{existingFamily.Name}' is already current (VersionGuid unchanged)");
#else
            SmartConLogger.Info($"[FamilyLoad][{attemptName}] Family '{existingFamily.Name}' is already current");
#endif
            return new FamilyLoadResult(true, existingFamily.Name,
                $"Family '{existingFamily.Name}' is already up-to-date", null, FamilyLoadStatus.Current);
        }

        SmartConLogger.Info($"[FamilyLoad][{attemptName}] Failed: loadedFamily is null or success=false");
        return null;
    }

    private static string BuildErrorMessage(string path)
    {
        var fileName = Path.GetFileName(path);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(path);

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

    public Task<FamilyLoadResult> LoadFamilyAsync(FamilyResolvedFile file, FamilyLoadOptions options, Action<string>? onStatusMessage = null, CancellationToken ct = default)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return Task.FromResult(new FamilyLoadResult(false, null, null, "No active document", FamilyLoadStatus.Failed));

        var normalizedPath = Path.GetFullPath(file.AbsolutePath);
        SmartConLogger.Info($"[FamilyLoad] Attempting to load family from: {normalizedPath}");
        SmartConLogger.Info($"[FamilyLoad] File exists: {File.Exists(normalizedPath)}");
        SmartConLogger.Info($"[FamilyLoad] Current Revit version: {doc.Application.VersionNumber}");

        if (!File.Exists(normalizedPath))
        {
            SmartConLogger.Info($"[FamilyLoad] File not found: {normalizedPath}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, $"File not found: {normalizedPath}", FamilyLoadStatus.Failed));
        }

        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            SmartConLogger.Info($"[FamilyLoad] File size: {fileInfo.Length} bytes");

            using var basicInfo = BasicFileInfo.Extract(normalizedPath);
            if (basicInfo != null)
            {
                var fileFormat = basicInfo.Format ?? "unknown";
                var isCurrentVersion = basicInfo.IsSavedInCurrentVersion;
                SmartConLogger.Info($"[FamilyLoad] File version={fileFormat}, IsCurrentVersion={isCurrentVersion}, LaterVersion={basicInfo.IsSavedInLaterVersion}");

                if (basicInfo.IsSavedInLaterVersion)
                {
                    SmartConLogger.Info($"[FamilyLoad] Family saved in newer version: {fileFormat}");
                    return Task.FromResult(new FamilyLoadResult(false, null, null,
                        $"Family was saved in Revit {fileFormat} and cannot be opened in the current version.", FamilyLoadStatus.Failed));
                }

                if (!isCurrentVersion)
                {
                    SmartConLogger.Info($"[FamilyLoad] UPGRADE DIALOG EXPECTED for {normalizedPath} (version {fileFormat})");
                }
            }

            var preferredName = options.PreferredName?.Trim();
            var checkName = !string.IsNullOrWhiteSpace(preferredName)
                ? preferredName
                : Path.GetFileNameWithoutExtension(normalizedPath).Trim();

            SmartConLogger.Info($"[FamilyLoad] Checking for existing family by name: '{checkName}'");

            var existingFamily = FindExistingFamily(doc, checkName!);
            if (existingFamily is not null)
            {
#if REVIT2021_OR_GREATER
                SmartConLogger.Info($"[FamilyLoad] Family '{checkName}' found in project (Id={existingFamily.Id}, VersionGuid={existingFamily.VersionGuid})");
#else
                SmartConLogger.Info($"[FamilyLoad] Family '{checkName}' found in project (Id={existingFamily.Id})");
#endif
            }
            else
            {
                SmartConLogger.Info($"[FamilyLoad] No existing family found with name '{checkName}'");
            }

            var loadOptions = new RevitFamilyLoadOptions(options.OverwriteParameterValues, onStatusMessage);

            SmartConLogger.Info("[FamilyLoad] Attempt 1: LoadFamily with options in transaction...");
            var result1 = TryLoadInTransaction(doc, normalizedPath, loadOptions, options, "Attempt1", existingFamily);
            if (result1 is not null)
                return Task.FromResult(result1);
            SmartConLogger.Info("[FamilyLoad] Attempt 1 failed (returned null)");

            SmartConLogger.Info("[FamilyLoad] Attempt 2: LoadFamily without IFamilyLoadOptions...");
            var result2 = TryLoadInTransaction(doc, normalizedPath, null, options, "Attempt2", existingFamily);
            if (result2 is not null)
                return Task.FromResult(result2);
            SmartConLogger.Info("[FamilyLoad] Attempt 2 failed (returned null)");

            var errorMessage = BuildErrorMessage(normalizedPath);
            SmartConLogger.Info($"[FamilyLoad] Both attempts failed - returning error: {errorMessage}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, errorMessage, FamilyLoadStatus.Failed));
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[FamilyLoad] LoadFamily exception: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, ex.Message, FamilyLoadStatus.Failed));
        }
    }
}
