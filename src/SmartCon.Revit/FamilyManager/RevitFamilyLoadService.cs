using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Services.FamilyLoading;

public sealed class RevitFamilyLoadService : IFamilyLoadService
{
    private readonly IRevitContext _revitContext;

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

    public RevitFamilyLoadService(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    /// <summary>Renames a family inside an active transaction. Returns the new name or null if unchanged/failed.</summary>
    private static string? TryRenameInsideTransaction(Autodesk.Revit.DB.Family family, string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName))
            return null;

        var currentName = family.Name;
        if (string.Equals(currentName, preferredName, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            family.Name = preferredName;
            SmartConLogger.Info($"[FamilyLoad] Renamed family from '{currentName}' to '{preferredName}'");
            return preferredName;
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[FamilyLoad] Failed to rename family: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
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

            var hashName = Path.GetFileNameWithoutExtension(normalizedPath);
            var checkName = !string.IsNullOrWhiteSpace(options.PreferredName)
                ? options.PreferredName
                : hashName;

            // Check if family already loaded by display name (or hash if no display name)
            var existingFamily = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Family))
                .Cast<Autodesk.Revit.DB.Family>()
                .FirstOrDefault(f => f.Name.Equals(checkName, StringComparison.OrdinalIgnoreCase));

            if (existingFamily != null)
            {
                SmartConLogger.Info($"[FamilyLoad] Family '{checkName}' already loaded in project");
                ActivateRevitWindow();
                return Task.FromResult(new FamilyLoadResult(true, existingFamily.Name, $"Family '{existingFamily.Name}' already loaded in project", null));
            }

            var loadOptions = new RevitFamilyLoadOptions();

            // Attempt 1: LoadFamily + rename inside single transaction (single undoable action)
            SmartConLogger.Info("[FamilyLoad] Attempt 1: LoadFamily with transaction...");
            using (var tx = new Transaction(doc, "Load Family"))
            {
                tx.Start();
                var loaded = doc.LoadFamily(normalizedPath, loadOptions, out Autodesk.Revit.DB.Family family);
                
                if (loaded && family is not null)
                {
                    var renamedName = TryRenameInsideTransaction(family, options.PreferredName);
                    var displayName = renamedName ?? family.Name;
                    tx.Commit();
                    SmartConLogger.Info($"[FamilyLoad] Successfully loaded family: {displayName}");
                    ActivateRevitWindow();
                    return Task.FromResult(new FamilyLoadResult(true, displayName, $"Family '{displayName}' loaded successfully", null));
                }
                
                tx.RollBack();
                SmartConLogger.Info("[FamilyLoad] Attempt 1 failed");
            }

            // Attempt 2: LoadFamily without IFamilyLoadOptions
            SmartConLogger.Info("[FamilyLoad] Attempt 2: LoadFamily without IFamilyLoadOptions...");
            using (var tx = new Transaction(doc, "Load Family Basic"))
            {
                tx.Start();
                var loaded = doc.LoadFamily(normalizedPath, out Autodesk.Revit.DB.Family family);
                
                if (loaded && family is not null)
                {
                    var renamedName = TryRenameInsideTransaction(family, options.PreferredName);
                    var displayName = renamedName ?? family.Name;
                    tx.Commit();
                    SmartConLogger.Info($"[FamilyLoad] Successfully loaded family (basic): {displayName}");
                    ActivateRevitWindow();
                    return Task.FromResult(new FamilyLoadResult(true, displayName, $"Family '{displayName}' loaded successfully", null));
                }
                
                tx.RollBack();
                SmartConLogger.Info("[FamilyLoad] Attempt 2 failed");
            }

            // Attempt 3: Load from temp copy
            var tempPath = Path.Combine(Path.GetTempPath(), $"SmartCon_Family_{Guid.NewGuid()}.rfa");
            try
            {
                File.Copy(normalizedPath, tempPath, overwrite: true);
                SmartConLogger.Info($"[FamilyLoad] Attempt 3: Loading from temp: {tempPath}");

                using (var tx = new Transaction(doc, "Load Family Temp"))
                {
                    tx.Start();
                    var loaded = doc.LoadFamily(tempPath, loadOptions, out Autodesk.Revit.DB.Family family);
                    
                    if (loaded && family is not null)
                    {
                        var renamedName = TryRenameInsideTransaction(family, options.PreferredName);
                        var displayName = renamedName ?? family.Name;
                        tx.Commit();
                        SmartConLogger.Info($"[FamilyLoad] Successfully loaded from temp: {displayName}");
                        ActivateRevitWindow();
                        return Task.FromResult(new FamilyLoadResult(true, displayName, $"Family '{displayName}' loaded successfully", null));
                    }
                    
                    tx.RollBack();
                }
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
