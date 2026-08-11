using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// FHV8 (#209, ADR-066): composite content hash over the shared-nested
/// closure on REAL extracted content. Proves the two assumptions the
/// design relies on:
/// 1) transitivity — a content change deep in the chain (grandchild)
///    shifts the composite hashes of every ancestor;
/// 2) standalone/nested equivalence — a family extracted standalone from
///    its .rfa (migration-time path) hashes identically to the same
///    family extracted as a nested EditFamily copy (import-time path).
/// Also locks extractor determinism: two independent EditFamily
/// extractions of the same content must compose to identical hashes.
/// </summary>
public sealed class CompositeFamilyHashTests : RevitApiTest
{
    private const string GrandchildName = "SmartConHashGrandchild";
    private const string ChildName = "SmartConHashChild";
    private const string ParentName = "SmartConHashParent";

    private string? _tempDir;
    private string? _childPath;
    private string? _grandchildPath;
    private Document? _parentDoc;

    private Document ParentDoc => _parentDoc!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void SeedFamilyChain()
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the nested family chain");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConHash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _grandchildPath = Path.Combine(_tempDir, GrandchildName + ".rfa");
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");

        CreateSharedFamily(template, _grandchildPath);
        CreateSharedFamily(template, _childPath, _grandchildPath);

        _parentDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(ParentDoc, "Load child"))
        {
            tx.Start();
            if (!ParentDoc.LoadFamily(_childPath, out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            tx.Commit();
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        try
        {
            if (_parentDoc is not null && _parentDoc.IsValidObject)
            {
                _parentDoc.Close(false);
            }
        }
        catch
        {
        }

        try
        {
            if (_tempDir is not null && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Test]
    public async Task CompositeHash_DeepNestedChange_TransitivelyShiftsAllAncestors()
    {
        var composer = new CompositeFamilyHashComposer(new FamilyContentHasher());

        var closure = ExtractClosure(ParentDoc, ParentName);
        var before = composer.Compose(closure.Snapshots, closure.Subtrees);

        // Extractor determinism: a SECOND independent EditFamily extraction
        // of the same content must compose to identical hashes.
        var closureAgain = ExtractClosure(ParentDoc, ParentName);
        var beforeAgain = composer.Compose(closureAgain.Snapshots, closureAgain.Subtrees);
        using (Assert.Multiple())
        {
            await Assert.That(before[ParentName]!.HexString).IsEqualTo(beforeAgain[ParentName]!.HexString);
            await Assert.That(before[ChildName]!.HexString).IsEqualTo(beforeAgain[ChildName]!.HexString);
            await Assert.That(before[GrandchildName]!.HexString).IsEqualTo(beforeAgain[GrandchildName]!.HexString);
        }

        // Modify the grandchild's OWN content (new family type) in its
        // standalone document — the bolt in the valve→flange→bolt chain.
        var grandchildDoc = Application.OpenDocumentFile(_grandchildPath!);
        FamilySnapshot modifiedGrandchild;
        try
        {
            using (var tx = new Transaction(grandchildDoc, "Add extra type"))
            {
                tx.Start();
                grandchildDoc.FamilyManager.NewType("ExtraProbeType");
                tx.Commit();
            }
            modifiedGrandchild = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(grandchildDoc);
        }
        finally
        {
            grandchildDoc.Close(false);
        }

        var changedSnapshots = new Dictionary<string, FamilySnapshot>(closure.Snapshots, StringComparer.OrdinalIgnoreCase)
        {
            [GrandchildName] = modifiedGrandchild,
        };
        var after = composer.Compose(changedSnapshots, closure.Subtrees);

        using (Assert.Multiple())
        {
            await Assert.That(after[GrandchildName]!.HexString).IsNotEqualTo(before[GrandchildName]!.HexString);
            await Assert.That(after[ChildName]!.HexString).IsNotEqualTo(before[ChildName]!.HexString);
            await Assert.That(after[ParentName]!.HexString).IsNotEqualTo(before[ParentName]!.HexString);
        }
        SmartConLogger.Info("FHV8 probe: deep nested change transitively shifted all ancestor hashes");
    }

    [Test]
    public async Task CompositeHash_StandaloneVsNestedExtraction_SameContent_SameHash()
    {
        var composer = new CompositeFamilyHashComposer(new FamilyContentHasher());

        // Import-time view: the child extracted as a nested EditFamily copy
        // from the parent's family document.
        var nestedClosure = ExtractClosure(ParentDoc, ParentName);
        var nestedHashes = composer.Compose(nestedClosure.Snapshots, nestedClosure.Subtrees);

        // Migration-time view: the child extracted standalone from its own
        // managed .rfa, its closure re-opened from ITS document.
        var childDoc = Application.OpenDocumentFile(_childPath!);
        Dictionary<string, FamilyContentHash?> standaloneHashes;
        Dictionary<string, FamilySnapshot> standaloneSnapshots;
        try
        {
            var standaloneClosure = ExtractClosure(childDoc, ChildName);
            standaloneSnapshots = standaloneClosure.Snapshots;
            standaloneHashes = new Dictionary<string, FamilyContentHash?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in composer.Compose(standaloneClosure.Snapshots, standaloneClosure.Subtrees))
            {
                standaloneHashes[pair.Key] = pair.Value;
            }
        }
        finally
        {
            childDoc.Close(false);
        }

        // Diagnostics: dump both contexts before asserting — a mismatch
        // must name the differing snapshot section, not just the hash.
        DumpSnapshot("nested-child", nestedClosure.Snapshots[ChildName]);
        DumpSnapshot("standalone-child", standaloneSnapshots[ChildName]);
        DumpSnapshot("nested-grandchild", nestedClosure.Snapshots[GrandchildName]);
        DumpSnapshot("standalone-grandchild", standaloneSnapshots[GrandchildName]);

        using (Assert.Multiple())
        {
            await Assert.That(nestedHashes[ChildName]!.HexString)
                .IsEqualTo(standaloneHashes[ChildName]!.HexString);
            await Assert.That(nestedHashes[GrandchildName]!.HexString)
                .IsEqualTo(standaloneHashes[GrandchildName]!.HexString);
        }
        SmartConLogger.Info("FHV8 probe: standalone extraction == nested EditFamily extraction (hash equivalence)");
    }

    private static void DumpSnapshot(string tag, FamilySnapshot s)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"DUMP {tag}: name='{s.FamilyName}' cat='{s.Category}' catId={s.CategoryId} ");
        sb.Append($"params={s.Parameters.Count} types={s.Types.Count} ");
        sb.Append($"geom(forms={s.Geometry.TotalFormCount},sym={s.Geometry.SymbolicCurveCount},det={s.Geometry.DetailCurveCount},mod={s.Geometry.ModelCurveCount},txt={s.Geometry.TextNoteCount},ref={s.Geometry.ReferencePlaneCount},dim={s.Geometry.DimensionCount}) ");
        sb.Append($"shared=[{string.Join(",", s.SharedNestedFamilyNames)}] ");
        sb.Append($"nonshared=[{string.Join(",", s.NonSharedNestedFamilyNames ?? (IReadOnlyList<string>)[])}] ");
        sb.Append($"facts={s.Facts?.Count ?? -1} conn={s.Connectors?.Count ?? -1} ");
        sb.Append($"flags={(s.BehaviorFlags is null ? "<null>" : $"{s.BehaviorFlags.IsShared},{s.BehaviorFlags.IsWorkPlaneBased},{s.BehaviorFlags.IsAlwaysVertical},{s.BehaviorFlags.AllowsCutWithVoids}")}");
        foreach (var p in s.Parameters.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append($"\n  PARAM {p.Name}|{p.StorageType}|{p.ParameterGroup}|{(p.IsInstance ? "I" : "T")}|{(p.IsShared ? "S" : "P")}|{p.Formula ?? "-"}|{p.SharedParamGuid ?? "-"}|{p.BuiltInParameterId ?? "-"}");
        }
        foreach (var t in s.Types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append($"\n  TYPE {t.Name} uid={t.UniqueId ?? "-"}");
            foreach (var v in t.Values.OrderBy(v => v.ParameterName, StringComparer.Ordinal))
            {
                sb.Append($"\n    VAL {v.ParameterName}|{v.StorageType}|has={v.HasValue}|txt={v.ValueText ?? "<null>"}|num={v.ValueNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}|elem={v.ResolvedElementName ?? "-"}");
            }
        }
        SmartConLogger.Info(sb.ToString());
    }

    /// <summary>
    /// Extracts the root document's own snapshot plus the whole shared-
    /// nested closure (flat scans per document, probe P1) — the same
    /// worklist shape as the production migration extractor
    /// (<see cref="RevitFamilyMigrationExtractor"/>). Nested documents are
    /// closed before returning; only the pure snapshots/scans survive.
    /// </summary>
    private static (Dictionary<string, FamilySnapshot> Snapshots, Dictionary<string, IReadOnlyList<string>> Subtrees)
        ExtractClosure(Document rootDoc, string rootName)
    {
        var collector = new RevitFamilyDependencyCollector();
        var extractor = new RevitFamilySnapshotExtractor();

        var snapshots = new Dictionary<string, FamilySnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [rootName] = extractor.ExtractFromFamilyDocument(rootDoc),
        };
        var subtrees = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var rootScan = collector.CollectSharedNestedDependencies(rootDoc);
        subtrees[rootName] = rootScan.Select(d => d.FamilyName).ToList();

        var nestedDocs = new List<Document>();
        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var worklist = new Queue<(Document OwnerDoc, FamilyDependencyDescriptor Descriptor)>(
                rootScan.Select(d => (rootDoc, d)));

            while (worklist.Count > 0)
            {
                var (ownerDoc, descriptor) = worklist.Dequeue();
                if (!visited.Add(descriptor.FamilyName))
                {
                    continue;
                }

                var family = ownerDoc.GetElement(descriptor.FamilyUniqueId) as Family
                    ?? throw new InvalidOperationException(
                        $"nested family '{descriptor.FamilyName}' not found in its owner document");
                var nestedDoc = ownerDoc.EditFamily(family);
                nestedDocs.Add(nestedDoc);

                snapshots[descriptor.FamilyName] = extractor.ExtractFromFamilyDocument(nestedDoc);
                var nestedScan = collector.CollectSharedNestedDependencies(nestedDoc);
                subtrees[descriptor.FamilyName] = nestedScan.Select(d => d.FamilyName).ToList();
                foreach (var child in nestedScan)
                {
                    worklist.Enqueue((nestedDoc, child));
                }
            }
        }
        finally
        {
            foreach (var nestedDoc in nestedDocs)
            {
                try { nestedDoc.Close(false); } catch { }
            }
        }

        return (snapshots, subtrees);
    }

    private void CreateSharedFamily(string template, string targetPath, string? nestedPathToLoad = null)
    {
        var doc = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(doc, "Mark shared + load nested"))
            {
                tx.Start();

                if (nestedPathToLoad is not null && !doc.LoadFamily(nestedPathToLoad, out _))
                {
                    throw new InvalidOperationException($"LoadFamily({nestedPathToLoad}) returned false");
                }

                var sharedParam = doc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED);
                if (sharedParam is not null && !sharedParam.IsReadOnly)
                {
                    sharedParam.Set(1);
                }

                tx.Commit();
            }

            doc.SaveAs(targetPath, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally
        {
            doc.Close(false);
        }
    }
}
