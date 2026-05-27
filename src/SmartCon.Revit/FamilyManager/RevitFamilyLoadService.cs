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

            if (!string.IsNullOrWhiteSpace(options.PreferredName)
                && !string.Equals(family.Name, options.PreferredName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    family.Name = options.PreferredName;
                    SmartConLogger.Info($"[FamilyLoad] Renamed family to '{options.PreferredName}'");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Info($"[FamilyLoad] Rename failed (non-fatal): {ex.Message}");
                }
            }
        });

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

    public Task<FamilyLoadResult> LoadFamilyAsync(FamilyResolvedFile file, FamilyLoadOptions options, CancellationToken ct = default)
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
                SmartConLogger.Freeze($"FamilyLoad: File version={fileFormat}, IsCurrentVersion={isCurrentVersion}, LaterVersion={basicInfo.IsSavedInLaterVersion}");

                if (basicInfo.IsSavedInLaterVersion)
                {
                    SmartConLogger.Info($"[FamilyLoad] Family saved in newer version: {fileFormat}");
                    return Task.FromResult(new FamilyLoadResult(false, null, null,
                        $"Family was saved in Revit {fileFormat} and cannot be opened in the current version.", FamilyLoadStatus.Failed));
                }

                if (!isCurrentVersion)
                {
                    SmartConLogger.Freeze($"FamilyLoad: UPGRADE DIALOG EXPECTED for {normalizedPath} (version {fileFormat})");
                }
            }

            var checkName = !string.IsNullOrWhiteSpace(options.PreferredName)
                ? options.PreferredName
                : Path.GetFileNameWithoutExtension(normalizedPath);

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

            var loadOptions = new RevitFamilyLoadOptions();

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

            var tempPath = Path.Combine(Path.GetTempPath(), $"SmartCon_Family_{Guid.NewGuid()}.rfa");
            try
            {
                File.Copy(normalizedPath, tempPath, overwrite: true);
                SmartConLogger.Info($"[FamilyLoad] Attempt 3: Loading from temp: {tempPath}");

                var result3 = TryLoadInTransaction(doc, tempPath, loadOptions, options, "Attempt3", existingFamily);
                if (result3 is not null)
                    return Task.FromResult(result3);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            SmartConLogger.Info("[FamilyLoad] All 3 attempts failed - returning error");
            return Task.FromResult(new FamilyLoadResult(false, null, null,
                "Unable to load family. The file may be from a newer Revit version or incompatible with this project.", FamilyLoadStatus.Failed));
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[FamilyLoad] LoadFamily exception: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, ex.Message, FamilyLoadStatus.Failed));
        }
    }
}
