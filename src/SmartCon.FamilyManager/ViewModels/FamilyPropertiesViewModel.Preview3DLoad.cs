using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Geometry;
using SmartCon.UI;
using Media3D = System.Windows.Media.Media3D;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
    /// <summary>
    /// Find the auto-extracted GLB asset for the given type, resolve its path,
    /// load it via <see cref="GlbSceneLoader"/>, and add it to
    /// <see cref="Scene3DRoot"/>. Clears any previously-loaded scene first.
    /// </summary>
    /// <param name="typeName">Type name from <see cref="Available3DTypeNames"/>.
    /// Must not be null/empty; callers should supply a valid type or defer.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task Load3DPreviewForTypeAsync(string? typeName, CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FMProperties3D",
            ("Method", nameof(Load3DPreviewForTypeAsync)),
            ("TypeName", typeName ?? "<null>"));

        SmartConLogger.Info(
            $"Load3DPreviewForTypeAsync: typeName={typeName ?? "<null>"}, " +
            $"EffectsManager3DIsNull={EffectsManager3D is null}, " +
            $"Model3DAssets.Count={Model3DAssets.Count}, " +
            $"Selected3DTypeName={Selected3DTypeName ?? "<null>"}");

        // Critical: HelixToolkit requires scene.Root.Attach(effectsManager)
        // BEFORE AddNode — otherwise mesh nodes never get GPU vertex buffers
        // allocated and the viewport renders black (issue helix-toolkit #2215).
        // If the viewer hasn't been initialized yet (View.Loaded hasn't fired),
        // defer the actual load until Initialize3DInfrastructure completes.
        if (EffectsManager3D is null)
        {
            SmartConLogger.Info(
                "Load3DPreviewForTypeAsync: EffectsManager3D is null — deferring load " +
                "until Initialize3DInfrastructure is called from View.Loaded");
            return;
        }

        // Guard against accidental null/empty typeName (e.g. ComboBox SelectedItem
        // reset during ItemsSource refresh). Without this guard the null suffix
        // would not match any existing GLB and would trigger an expensive on-demand
        // .rfa extraction.
        if (string.IsNullOrEmpty(typeName))
        {
            Has3DPreview = false;
            Preview3DStatusMessage = LanguageManager.GetString(
                StringLocalization.Keys.FM_3D_NoPreview) ?? "No 3D preview for this version";
            SmartConLogger.Info(
                "Load3DPreviewForTypeAsync: typeName is null/empty and no explicit type " +
                "was requested; skipping preview load to avoid on-demand .rfa extraction");
            return;
        }

        try
        {
            IsLoading3D = true;
            Preview3DStatusMessage = null;

            // Preserve the user's camera when only switching types. The first
            // load (or any load after a version/family change) should fit the
            // camera to the new scene; subsequent type swaps keep the camera.
            var preserveCamera = false;
            if (!_isFirst3DLoad && Camera3D is { } camera)
            {
                preserveCamera = true;
                _savedCameraPosition = camera.Position;
                _savedCameraLookDirection = camera.LookDirection;
                _savedCameraUpDirection = camera.UpDirection;
                SmartConLogger.Debug("Load3DPreviewForTypeAsync: preserving user camera for type swap");
            }

            Scene3DRoot.Clear();

            var typeSuffix = typeName is null ? "" : typeName;
            var descriptionSuffix = "::" + typeSuffix;

            var autoAsset = Model3DAssets.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.Description)
                && a.Description!.EndsWith(descriptionSuffix, System.StringComparison.Ordinal)
                && a.Description.StartsWith(AutoExtractedPreviewPrefix, System.StringComparison.Ordinal)
                && string.Equals(a.VersionLabel, VersionLabel, System.StringComparison.Ordinal));

            if (autoAsset is null)
            {
                Has3DPreview = false;
                Preview3DStatusMessage = LanguageManager.GetString(
                    StringLocalization.Keys.FM_3D_NoPreview) ?? "No 3D preview for this version";
                var availableDescriptions = string.Join("; ", Model3DAssets
                    .Where(a => !string.IsNullOrEmpty(a.Description))
                    .Select(a => $"'{a.Description}' (v={a.VersionLabel ?? "<null>"})"));
                SmartConLogger.Warn(
                    $"No auto-extracted GLB asset found for type='{typeName}', VersionLabel='{VersionLabel ?? "<null>"}', " +
                    $"Model3DAssets.Count={Model3DAssets.Count}, matching descriptions=[{availableDescriptions}] " +
                    "[Action: re-import the family, or check that the version has visible 3D solids]");

                // Lazy fallback: try to extract geometry now if the import-time
                // pipeline missed it (race, failure, or pre-ADR-042 catalog).
                _ = TryExtract3DPreviewOnDemandAsync(typeName, ct);
                return;
            }

            var glbPath = await _assetService.ResolveAssetPathAsync(autoAsset.Id, ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(glbPath))
            {
                Has3DPreview = false;
                Preview3DStatusMessage = LanguageManager.GetString(
                    StringLocalization.Keys.FM_3D_NoPreview) ?? "No 3D preview for this version";
                SmartConLogger.Warn(
                    $"GLB asset '{autoAsset.FileName}' resolved to null path " +
                    "[Action: re-import the family to regenerate the GLB, or check managed storage]");
                return;
            }

            // Offload I/O + Assimp parse to ThreadPool (keeps UI responsive
            // for large meshes; Assimp is C++/P-Invoke so ThreadPool-safe).
            var scene = await Task.Run(
                () => GlbSceneLoader.LoadScene(glbPath!),
                ct).ConfigureAwait(true);

            if (scene is null)
            {
                Has3DPreview = false;
                Preview3DStatusMessage = LanguageManager.GetString(
                    StringLocalization.Keys.FM_3D_LoadFailed) ?? "Failed to load 3D preview";
                return;
            }

            // scene.Attach creates GPU vertex buffers for each node.
            scene.Attach(EffectsManager3D);
            SmartConLogger.Info("scene.Attach(EffectsManager3D) done");

            Scene3DRoot.AddNode(scene);
            SmartConLogger.Info("3D preview loaded and added to Scene3DRoot");

            // Walk the loaded scene: ensure every MeshNode has a non-null
            // MaterialCore. Assimp importer sometimes creates meshes without
            // materials, which renders as invisible.
            int nullMaterialCount = 0;
            int totalMeshCount = 0;
            EnsureMaterialsRecursive(scene, ref nullMaterialCount, ref totalMeshCount);
            SmartConLogger.Info(
                $"EnsureMaterials: {totalMeshCount} meshes scanned, " +
                $"{nullMaterialCount} had null Material and received default PhongMaterialCore");

            // Compute world-space transforms after AddNode so the render host
            // has correct matrices for the vertex shader (issue #2215, #2013).
            scene.UpdateAllTransformMatrix();
            SmartConLogger.Info("scene.UpdateAllTransformMatrix() done");

            if (_isFirst3DLoad)
            {
                FitCameraToScene();
                _isFirst3DLoad = false;
            }
            else if (preserveCamera)
            {
                RestoreCameraState();
            }

            Has3DPreview = true;
        }
        catch (OperationCanceledException)
        {
            Has3DPreview = false;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Load3DPreviewForTypeAsync failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: 3D preview will show error overlay; check smartcon.log for details; " +
                "re-import the family if the GLB file is corrupted]");
            Has3DPreview = false;
            Preview3DStatusMessage = LanguageManager.GetString(
                StringLocalization.Keys.FM_3D_LoadFailed) ?? "Failed to load 3D preview";
        }
        finally
        {
            IsLoading3D = false;
        }
    }

    /// <summary>
    /// Lazy fallback: when no auto-extracted GLB asset exists for the current
    /// version, resolve the managed .rfa path and run the geometry pipeline
    /// on demand. This covers cases where the import-time hook failed or the
    /// catalog predates ADR-042. The pipeline is fire-and-forget from the UI
    /// perspective; when it completes, the preview is reloaded.
    /// </summary>
    private async Task TryExtract3DPreviewOnDemandAsync(string? typeName, CancellationToken ct)
    {
        SmartConLogger.Info(
            $"TryExtract3DPreviewOnDemandAsync: ENTER typeName={typeName ?? "<null>"}, " +
            $"VersionLabel={VersionLabel ?? "<null>"}, _isLazy3DExtractionRunning={_isLazy3DExtractionRunning}");

        if (_isLazy3DExtractionRunning)
        {
            SmartConLogger.Info("TryExtract3DPreviewOnDemandAsync: already running, skipping");
            return;
        }

        if (string.IsNullOrEmpty(typeName))
        {
            SmartConLogger.Info(
                "TryExtract3DPreviewOnDemandAsync: typeName is null/empty, cannot extract geometry for unnamed type");
            return;
        }

        if (string.IsNullOrEmpty(VersionLabel))
        {
            SmartConLogger.Info(
                "TryExtract3DPreviewOnDemandAsync: VersionLabel is null/empty, cannot resolve managed .rfa");
            return;
        }

        _isLazy3DExtractionRunning = true;
        try
        {
            using var _scope = SmartConLogger.BeginScope("FMProperties3D",
                ("Method", nameof(TryExtract3DPreviewOnDemandAsync)),
                ("VersionLabel", VersionLabel!));

            SmartConLogger.Info(
                $"TryExtract3DPreviewOnDemandAsync: resolving managed .rfa for " +
                $"catalogItemId='{_catalogItemId}', versionLabel='{VersionLabel}'");

            var resolved = await _fileResolver.ResolveVersionAsync(_catalogItemId, VersionLabel!, ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(resolved.AbsolutePath) || !File.Exists(resolved.AbsolutePath))
            {
                SmartConLogger.Warn(
                    $"TryExtract3DPreviewOnDemandAsync: managed .rfa not found for version '{VersionLabel}' " +
                    "[Action: verify the version file exists in managed storage]");
                return;
            }

            SmartConLogger.Info(
                $"TryExtract3DPreviewOnDemandAsync: running geometry pipeline for '{Path.GetFileName(resolved.AbsolutePath)}'");

            await _geometryPipeline.RunAsync(
                null,
                resolved.AbsolutePath,
                _catalogItemId,
                resolved.VersionId ?? Guid.NewGuid().ToString("N"),
                VersionLabel!,
                Name,
                ct: ct).ConfigureAwait(true);

            SmartConLogger.Info(
                "TryExtract3DPreviewOnDemandAsync: pipeline completed, reloading assets and preview");

            await LoadAssetsAsync(ct).ConfigureAwait(true);
            await Load3DPreviewForTypeAsync(typeName, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"TryExtract3DPreviewOnDemandAsync failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: 3D preview will remain unavailable; check smartcon.log for details]");
        }
        finally
        {
            _isLazy3DExtractionRunning = false;
        }
    }
}
