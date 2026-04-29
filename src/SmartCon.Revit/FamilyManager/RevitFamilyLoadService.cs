using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed class RevitFamilyLoadService : IFamilyLoadService
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _transactionService;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void ActivateRevitWindow()
    {
        try
        {
            var hWnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hWnd != IntPtr.Zero)
                SetForegroundWindow(hWnd);
        }
        catch { /* Best effort */ }
    }

    public RevitFamilyLoadService(IRevitContext revitContext, ITransactionService transactionService)
    {
        _revitContext = revitContext;
        _transactionService = transactionService;
    }

    private FamilyLoadResult? TryLoadInTransaction(
        Document doc, string path, RevitFamilyLoadOptions? loadOptions, FamilyLoadOptions options)
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
            SmartConLogger.Info($"[FamilyLoad] Successfully loaded family: {displayName}");
            ActivateRevitWindow();
            return new FamilyLoadResult(true, displayName, $"Family '{displayName}' loaded successfully", null);
        }

        return null;
    }

    public Task<FamilyLoadResult> LoadFamilyAsync(FamilyResolvedFile file, FamilyLoadOptions options, CancellationToken ct = default)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return Task.FromResult(new FamilyLoadResult(false, null, null, "No active document"));

        var normalizedPath = Path.GetFullPath(file.AbsolutePath);
        SmartConLogger.Info($"[FamilyLoad] Attempting to load family from: {normalizedPath}");
        SmartConLogger.Info($"[FamilyLoad] File exists: {File.Exists(normalizedPath)}");
        SmartConLogger.Info($"[FamilyLoad] Current Revit version: {doc.Application.VersionNumber}");

        if (!File.Exists(normalizedPath))
        {
            SmartConLogger.Info($"[FamilyLoad] File not found: {normalizedPath}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, $"File not found: {normalizedPath}"));
        }

        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            SmartConLogger.Info($"[FamilyLoad] File size: {fileInfo.Length} bytes");

            var checkName = !string.IsNullOrWhiteSpace(options.PreferredName)
                ? options.PreferredName
                : Path.GetFileNameWithoutExtension(normalizedPath);

            var existingFamily = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Family))
                .Cast<Autodesk.Revit.DB.Family>()
                .FirstOrDefault(f => f.Name.Equals(checkName, StringComparison.OrdinalIgnoreCase));

            if (existingFamily != null)
            {
                SmartConLogger.Info($"[FamilyLoad] Family '{checkName}' already loaded in project");
                ActivateRevitWindow();
                return Task.FromResult(new FamilyLoadResult(true, existingFamily.Name,
                    $"Family '{existingFamily.Name}' already loaded in project", null));
            }

            var loadOptions = new RevitFamilyLoadOptions();

            SmartConLogger.Info("[FamilyLoad] Attempt 1: LoadFamily with options in transaction...");
            var result1 = TryLoadInTransaction(doc, normalizedPath, loadOptions, options);
            if (result1 is not null)
                return Task.FromResult(result1);
            SmartConLogger.Info("[FamilyLoad] Attempt 1 failed");

            SmartConLogger.Info("[FamilyLoad] Attempt 2: LoadFamily without IFamilyLoadOptions...");
            var result2 = TryLoadInTransaction(doc, normalizedPath, null, options);
            if (result2 is not null)
                return Task.FromResult(result2);
            SmartConLogger.Info("[FamilyLoad] Attempt 2 failed");

            var tempPath = Path.Combine(Path.GetTempPath(), $"SmartCon_Family_{Guid.NewGuid()}.rfa");
            try
            {
                File.Copy(normalizedPath, tempPath, overwrite: true);
                SmartConLogger.Info($"[FamilyLoad] Attempt 3: Loading from temp: {tempPath}");

                var result3 = TryLoadInTransaction(doc, tempPath, loadOptions, options);
                if (result3 is not null)
                    return Task.FromResult(result3);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            SmartConLogger.Info("[FamilyLoad] All attempts failed");
            return Task.FromResult(new FamilyLoadResult(false, null, null,
                "Unable to load family. The file may be from a newer Revit version or incompatible with this project."));
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[FamilyLoad] LoadFamily exception: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(new FamilyLoadResult(false, null, null, ex.Message));
        }
    }
}
