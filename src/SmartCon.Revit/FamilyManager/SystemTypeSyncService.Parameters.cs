using System.Collections;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Electrical = Autodesk.Revit.DB.Electrical;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class SystemTypeSyncService
{
    private (int Written, int Skipped) WriteParameters(
        Document sourceDoc,
        Document doc,
        ElementType target,
        SystemTypeSnapshot template,
        Dictionary<string, ElementId?> elementIdCache,
        IReadOnlyDictionary<string, Guid>? portedGuids = null)
    {
        var written = 0;
        var skipped = 0;
        // Every skip is logged with its reason: ~30 params per type is far
        // from hot-loop volume, and an unexplained skip list was a blind
        // spot when diagnosing "the parameter did not sync" reports.
        var skippedDetails = new List<string>();

        foreach (var value in template.Values)
        {
            Parameter? param = null;
            // Just-ported shared parameters are addressed by GUID —
            // LookupParameter is ambiguous when same-name definitions exist
            // and was observed to miss freshly bound parameters entirely.
            if (portedGuids is not null
                && portedGuids.TryGetValue(value.ParameterName, out var portedGuid))
            {
                try { param = target.get_Parameter(portedGuid); } catch { /* fall through */ }
            }
            if (param is null)
            {
                try { param = target.LookupParameter(value.ParameterName); }
                catch { /* duplicate-name definitions — treated as missing */ }
            }

            if (param is null || param.IsReadOnly)
            {
                skipped++;
                skippedDetails.Add(param is null
                    ? $"{value.ParameterName}(missing)"
                    : $"{value.ParameterName}(read-only)");
                continue;
            }

            if (!value.HasValue)
            {
                if (param.HasValue)
                {
                    try
                    {
                        param.ClearValue();
                        written++;
                    }
                    catch (Exception ex)
                    {
                        // Revit: ClearValue is only allowed on shared
                        // parameters. For plain string parameters an empty
                        // string is the canonical "no value" state.
                        var cleared = false;
                        if (param.StorageType == StorageType.String)
                        {
                            try { cleared = param.Set(string.Empty); }
                            catch { /* counted below */ }
                        }
                        if (cleared) written++;
                        else
                        {
                            SmartConLogger.Debug(
                                $"Parameter '{value.ParameterName}' clear failed: {ex.Message}");
                            skipped++;
                            skippedDetails.Add($"{value.ParameterName}(clear-failed)");
                        }
                    }
                }
                continue;
            }

            var ok = false;
            try
            {
                ok = param.StorageType switch
                {
                    StorageType.Double => value.ValueNumber.HasValue && param.Set(value.ValueNumber.Value),
                    StorageType.Integer => value.ValueNumber.HasValue && param.Set((int)value.ValueNumber.Value),
                    StorageType.String => param.Set(value.ValueText ?? string.Empty),
                    StorageType.ElementId => TrySetElementId(sourceDoc, doc, param, value, elementIdCache),
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"Parameter '{value.ParameterName}' write failed: {ex.Message}");
                skippedDetails.Add($"{value.ParameterName}(set-failed)");
            }

            if (ok) written++;
            else
            {
                skipped++;
                // Set returned false without throwing — not covered by the
                // catch above, record the reason explicitly.
                if (!skippedDetails.Any(d => d.StartsWith(value.ParameterName + "(", StringComparison.Ordinal)))
                {
                    skippedDetails.Add($"{value.ParameterName}(set-rejected)");
                }
            }
        }

        if (skippedDetails.Count > 0)
        {
            SmartConLogger.Debug(
                $"Skipped parameters for '{target.Name}': [{string.Join(", ", skippedDetails)}]");
        }

        return (written, skipped);
    }

    /// <summary>
    /// The clean/staging project template carries no shared project
    /// parameters of the source (ADSK_*/BP_* etc.) — without porting their
    /// definitions the staged type loses them, and any reimport from the
    /// mini-project then sees a phantom VALUES diff against the version
    /// imported from the live project. Port every missing shared definition
    /// (same GUID, same data type, type-bound to the target category) so the
    /// staged file is a parameter-complete copy of the source type.
    /// </summary>
    private Dictionary<string, Guid> EnsureMissingSharedParameters(
        Document sourceDoc,
        Document doc,
        ElementType? sourceType,
        ElementType target,
        SystemTypeSnapshot template)
    {
        var portedGuids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (sourceType is null) return portedGuids;

        // Presence is checked via Element.Parameters — LookupParameter is
        // unreliable when same-name definitions exist (it may throw or pick
        // a ghost clone).
        var targetNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Parameter p in target.Parameters)
        {
            var n = p.Definition?.Name;
            if (!string.IsNullOrEmpty(n)) targetNames.Add(n!);
        }

        var missingSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in template.Values)
        {
            if (!targetNames.Contains(value.ParameterName))
                missingSet.Add(value.ParameterName);
        }
        if (missingSet.Count == 0) return portedGuids;

        // Resolve the EXACT definition each missing parameter is bound to in
        // the source (InternalDefinition.Id → ParameterElement). A name
        // search is NOT safe: a project can carry several definitions with
        // the same name (different GUIDs — e.g. a visible parameter plus an
        // invisible clone, BOTH enumerated by Element.Parameters). Prefer
        // the definition that carries a value; porting the empty clone
        // produces a phantom VALUES diff on mini-project reimport.
        // SHARED parameters keep their GUID. Non-shared PROJECT parameters
        // cannot be recreated as such (no public API) — they are ported as
        // shared definitions with a fresh GUID: the canonical VALUES string
        // carries name/storage/value only (no GUID), so the staged file
        // stays hash-identical to the source (owner stress test 2026-08-30:
        // the «Тип трубопровода» project parameter was lost in staging and
        // every reimport produced a phantom version).
        var chosen = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (Parameter sp in sourceType.Parameters)
        {
            var n = sp.Definition?.Name;
            if (string.IsNullOrEmpty(n) || !missingSet.Contains(n!)) continue;
            if (sp.Definition is not InternalDefinition internalDef
                || internalDef.BuiltInParameter != BuiltInParameter.INVALID)
                continue; // built-ins already exist on the target
            if (!chosen.TryGetValue(n!, out var current) || (!current.HasValue && sp.HasValue))
                chosen[n!] = sp;
        }

        var portPlan = new List<(string Name, Guid Guid, Parameter SourceParam)>();
        foreach (var missingName in missingSet)
        {
            if (!chosen.TryGetValue(missingName, out var sourceParam)) continue;

            Guid portGuid;
            if (sourceParam.IsShared
                && sourceParam.Definition is InternalDefinition sharedDef
                && sourceDoc.GetElement(sharedDef.Id) is SharedParameterElement sharedElement)
            {
                portGuid = sharedElement.GuidValue;
            }
            else if (TryFindPortedDefinition(doc, missingName, null, out var earlierGuid))
            {
                // Audit L23: an earlier type of this batch already ported a
                // NON-shared parameter under the same name — reuse its GUID
                // (and extend its binding below) instead of creating a
                // same-name duplicate definition.
                portGuid = earlierGuid;
            }
            else
            {
                portGuid = Guid.NewGuid();
            }

            portPlan.Add((missingName, portGuid, sourceParam));
        }
        if (portPlan.Count == 0)
        {
            SmartConLogger.Debug(
                $"Staged type '{target.Name}': {missingSet.Count} parameter(s) missing in the target " +
                "but none is a portable custom parameter of the source — nothing to port");
            return portedGuids;
        }

        var app = doc.Application;
        var originalFile = app.SharedParametersFilename;
        var tempFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"smartcon-shared-{Guid.NewGuid():N}.txt");
        var created = 0;
        var failed = new List<string>();
        try
        {
            System.IO.File.WriteAllText(tempFile, string.Empty);
            app.SharedParametersFilename = tempFile;
            var definitionFile = app.OpenSharedParameterFile();
            if (definitionFile is null)
            {
                SmartConLogger.Warn(
                    "Shared parameter port: OpenSharedParameterFile returned null for the temp file. " +
                    "[Action: staged types stay parameter-incomplete; reimport will show a VALUES diff]");
                return portedGuids;
            }

            var group = definitionFile.Groups.Create("SmartCon");
            foreach (var (name, portGuid, sourceParam) in portPlan)
            {
                var options = CreatePortedDefinitionOptions(name, sourceParam);
                if (options is null)
                {
                    failed.Add(name);
                    continue;
                }
                options.GUID = portGuid;
                // The source parameter reached the snapshot via
                // Element.Parameters, i.e. it is visible there — the ported
                // definition must stay visible or the extraction on reimport
                // would skip it.
                options.Visible = true;

                var definition = group.Definitions.Create(options);
                if (definition is null)
                {
                    failed.Add(name);
                    continue;
                }

                var categories = app.Create.NewCategorySet();
                categories.Insert(target.Category);
                var binding = app.Create.NewTypeBinding(categories);
                if (doc.ParameterBindings.Insert(definition, binding))
                {
                    created++;
                    portedGuids[name] = portGuid;
                }
                // Audit L23: a binding for this parameter already exists
                // (shared GUID ported for another category of this batch,
                // or the same non-shared name ported earlier) — extend it
                // with this type's category instead of failing.
                else if (TryExtendPortedBinding(doc, name, portGuid, target.Category))
                {
                    created++;
                    portedGuids[name] = portGuid;
                }
                else failed.Add(name);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Shared parameter port to the staged project failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: staged types stay parameter-incomplete; reimport will show a VALUES diff]");
        }
        finally
        {
            try { app.SharedParametersFilename = originalFile; } catch { /* app-level state, best effort */ }
            try { System.IO.File.Delete(tempFile); } catch { /* temp residue is harmless */ }
        }

        if (created > 0)
        {
            SmartConLogger.Info(
                $"Staged type '{target.Name}': ported {created} shared parameter definition(s) " +
                $"from the source project: [{string.Join(", ", portedGuids.Keys)}]");
        }
        if (failed.Count > 0)
        {
            SmartConLogger.Debug(
                $"Staged type '{target.Name}': shared parameter port failed for [{string.Join(", ", failed)}]");
        }
        return portedGuids;
    }

    /// <summary>
    /// Finds a shared parameter definition already ported into the document
    /// by name (and optionally by GUID). Used by the batch port (audit L23).
    /// </summary>
    private static bool TryFindPortedDefinition(Document doc, string name, Guid? guid, out Guid foundGuid)
    {
        var iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition internalDef
                || !string.Equals(internalDef.Name, name, StringComparison.Ordinal))
                continue;
            if (doc.GetElement(internalDef.Id) is not SharedParameterElement sharedElement)
                continue;
            if (guid is not null && sharedElement.GuidValue != guid.Value)
                continue;
            foundGuid = sharedElement.GuidValue;
            return true;
        }
        foundGuid = Guid.Empty;
        return false;
    }

    /// <summary>
    /// Adds the category to the existing ported binding of the same
    /// name/GUID (audit L23) — <c>ParameterBindings.Insert</c> rejects a
    /// second binding of an already-bound definition.
    /// </summary>
    private static bool TryExtendPortedBinding(Document doc, string name, Guid guid, Category category)
    {
        var map = doc.ParameterBindings;
        var iterator = map.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition internalDef
                || !string.Equals(internalDef.Name, name, StringComparison.Ordinal))
                continue;
            if (doc.GetElement(internalDef.Id) is not SharedParameterElement sharedElement
                || sharedElement.GuidValue != guid)
                continue;
            if (iterator.Current is not ElementBinding existingBinding
                || existingBinding.Categories.Contains(category))
                return true; // already bound for this category — nothing to do
            existingBinding.Categories.Insert(category);
            return map.ReInsert(internalDef, existingBinding);
        }
        return false;
    }

    private static ExternalDefinitionCreationOptions? CreatePortedDefinitionOptions(
        string name, Parameter sourceParam)
    {
#if REVIT2022_OR_GREATER
        var dataType = Compatibility.RevitUnitsCompat.GetDataType(sourceParam.Definition);
        if (dataType is null) return null;
        return new ExternalDefinitionCreationOptions(name, dataType);
#else
        try
        {
#pragma warning disable CS0618
            return new ExternalDefinitionCreationOptions(name, sourceParam.Definition.ParameterType);
#pragma warning restore CS0618
        }
        catch
        {
            return null;
        }
#endif
    }

    private bool TrySetElementId(
        Document sourceDoc,
        Document doc,
        Parameter param,
        SystemParameterValue value,
        Dictionary<string, ElementId?> cache)
    {
        var name = value.ResolvedElementName;
        if (string.IsNullOrEmpty(name)) return false;

        if (!cache.TryGetValue(name!, out var resolved))
        {
            resolved = ResolveElementByName(doc, name!);
            if (resolved is null)
            {
                // Last resort: a material with this name exists in the
                // reference but not in the project — create/update it.
                resolved = _materialSync.SyncMaterial(sourceDoc, doc, name!);
            }
            cache[name!] = resolved;
        }
        if (resolved is null) return false;

        try
        {
            return param.Set(resolved);
        }
        catch
        {
            return false;
        }
    }

    private static ElementId? ResolveElementByName(Document doc, string name)
    {
        using var materials = new FilteredElementCollector(doc).OfClass(typeof(Material));
        var material = materials.Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (material is not null) return material.Id;

        using var types = new FilteredElementCollector(doc).OfClass(typeof(ElementType));
        var type = types.Cast<ElementType>()
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        return type?.Id;
    }

    // ── #184 / ADR-065: stairs subtypes + railing structure ─────────────
}
