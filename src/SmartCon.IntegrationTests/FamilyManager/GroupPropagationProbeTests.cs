using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// #180 follow-up CONTRACT (probe 2026-08-12, outcome B): the production
/// poke + doc-to-doc merge
/// (<see cref="RevitFamilyLoadService.ReloadNestedInFamilyDocument"/>)
/// never propagates an IN-PLACE parameter regroup into the embedded nested
/// definition, although the merge itself really lands (a new type arrives
/// AND the embedded parameter is even re-created — its ElementId changes —
/// yet the group stays the host's). Proven with an in-place regroup
/// (<c>SetGroupTypeId</c>, R24+; same parameter identity in the source
/// file). Pre-2024 the API cannot regroup in place; the remove+add
/// fallback is delete + genuinely-new parameter, and the new parameter
/// lands WITH the file's group (observed on Revit 2023) — correct
/// new-parameter semantics, not a group propagation. Either way this pins
/// ADR-068 wall 2 for the poke path — and the reason FHV10 (the unified
/// hash) does not treat groups as content at all.
/// </summary>
public sealed class GroupPropagationProbeTests : RevitApiTest
{
    private const string ChildName = "SmartConGroupProbeChild";
    private const string ParamName = "P0";

    private string? _tempDir;
    private string? _childPath;
    private Document? _hostDoc;
    private string? _embeddedGroupBefore;
    private string? _fileGroupAfterV2;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the group propagation probe");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConGroupProbe_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");

        // Child v1: shared, P0 in group General, one named type.
        var childDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(childDoc, "v1"))
        {
            tx.Start();
            childDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            AddTextParameter(childDoc, generalGroup: true);
            childDoc.FamilyManager.NewType("TypeA");
            tx.Commit();
        }
        childDoc.SaveAs(_childPath!, new SaveAsOptions { OverwriteExistingFile = true });
        // Seed-verify (probe rule): the file really carries P0 in General.
        var seedGroup = ReadParamGroup(childDoc, out var seedId);
        SmartConLogger.Info($"PROBE_GROUP: seed file P0 group={seedGroup} id={seedId}");
        childDoc.Close(false);

        _hostDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(_hostDoc, "Load child"))
        {
            tx.Start();
            if (!_hostDoc.LoadFamily(_childPath!, out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            tx.Commit();
        }

        var nested = FindNestedFamily(_hostDoc, ChildName);
        var beforeCopy = _hostDoc.EditFamily(nested);
        try
        {
            _embeddedGroupBefore = ReadParamGroup(beforeCopy, out var beforeId);
            SmartConLogger.Info($"PROBE_GROUP: embedded P0 BEFORE merge group={_embeddedGroupBefore} id={beforeId}");
        }
        finally
        {
            beforeCopy.Close(false);
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        try
        {
            if (_hostDoc is not null && _hostDoc.IsValidObject)
            {
                _hostDoc.Close(false);
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
    public async Task PokeMerge_ParameterGroup_DoesNotPropagate()
    {
        BumpChildToV2();

        // Sanity: the v2 file really carries the regroup — otherwise the
        // contract below is vacuous.
        await Assert.That(_fileGroupAfterV2).IsNotNull();
        await Assert.That(_fileGroupAfterV2).IsNotEqualTo(_embeddedGroupBefore);

        var context = new StubRevitContext(_hostDoc!);
        var reload = new RevitFamilyLoadService(context, new RevitTransactionService(context))
            .ReloadNestedInFamilyDocument(_childPath!, ChildName, overwriteParameterValues: true);
        SmartConLogger.Info(
            $"PROBE_GROUP: reload success={reload.Success} status={reload.Status} error={reload.ErrorMessage ?? "<none>"}");
        if (!reload.Success)
        {
            throw new InvalidOperationException($"Nested reload failed: {reload.ErrorMessage}");
        }

        var nested = FindNestedFamily(_hostDoc!, ChildName);
        var copy = _hostDoc!.EditFamily(nested);
        try
        {
            var afterGroup = ReadParamGroup(copy, out var afterId);
            SmartConLogger.Info($"PROBE_GROUP: embedded P0 AFTER merge group={afterGroup} id={afterId}");

            var typeNames = new List<string>();
            foreach (FamilyType t in copy.FamilyManager.Types)
            {
                typeNames.Add(t.Name);
            }
            SmartConLogger.Info($"PROBE_GROUP: embedded types after merge = [{string.Join(",", typeNames)}]");

            // Merge control: v2's TypeB MUST have landed — the merge is real.
            await Assert.That(typeNames).Contains("TypeB");
#if REVIT2024_OR_GREATER
            // The contract: an IN-PLACE regroup (what the family-editor UI
            // does) never propagates — the embedded parameter keeps the
            // host's group although the merge landed (and even though the
            // embedded parameter object itself was re-created: its
            // ElementId changed 3698→4491 on the probe run).
            await Assert.That(afterGroup).IsEqualTo(_embeddedGroupBefore);
#else
            // remove+add is NOT a regroup — it is delete + genuinely-new
            // parameter, and the new parameter lands WITH the file's group
            // (observed on Revit 2023: embedded group became PG_DATA).
            // Pre-2024 API cannot simulate an in-place regroup
            // (SetGroupTypeId arrived in 2024), so the strong "never
            // propagates" contract is pinned on R24+ only. Either way the
            // verification-grade hash is unaffected: it excludes groups.
            await Assert.That(afterGroup).IsEqualTo(_fileGroupAfterV2);
#endif
        }
        finally
        {
            copy.Close(false);
        }
    }

    private void BumpChildToV2()
    {
        var doc = Application.OpenDocumentFile(_childPath!);
        try
        {
            string method;
#if REVIT2024_OR_GREATER
            // In-place regroup: same parameter identity, only the group
            // changes — the faithful simulation of a UI regroup.
            method = "SetGroupTypeId(in-place)";
            using (var tx = new Transaction(doc, "v2 regroup"))
            {
                tx.Start();
                var p0 = doc.FamilyManager.GetParameters()
                    .First(x => string.Equals(x.Definition?.Name, ParamName, StringComparison.Ordinal));
                ((InternalDefinition)p0.Definition).SetGroupTypeId(GroupTypeId.Data);
                doc.FamilyManager.NewType("TypeB");
                tx.Commit();
            }
#else
            method = "remove+add";
            using (var tx = new Transaction(doc, "v2 remove"))
            {
                tx.Start();
                var p0 = doc.FamilyManager.GetParameters()
                    .First(x => string.Equals(x.Definition?.Name, ParamName, StringComparison.Ordinal));
                doc.FamilyManager.RemoveParameter(p0);
                tx.Commit();
            }
            using (var tx = new Transaction(doc, "v2 re-add in Data"))
            {
                tx.Start();
                AddTextParameter(doc, generalGroup: false);
                doc.FamilyManager.NewType("TypeB");
                tx.Commit();
            }
#endif
            // Control: the saved v2 file really carries P0 in Data.
            _fileGroupAfterV2 = ReadParamGroup(doc, out var fileId);
            SmartConLogger.Info($"PROBE_GROUP: v2 file P0 group={_fileGroupAfterV2} id={fileId} (regroup method={method})");
            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    private static void AddTextParameter(Document doc, bool generalGroup)
    {
#if REVIT2022_OR_GREATER
        doc.FamilyManager.AddParameter(
            ParamName,
            generalGroup ? GroupTypeId.General : GroupTypeId.Data,
            SpecTypeId.String.Text,
            false);
#else
        doc.FamilyManager.AddParameter(
            ParamName,
            generalGroup ? BuiltInParameterGroup.PG_GENERAL : BuiltInParameterGroup.PG_DATA,
            ParameterType.Text,
            false);
#endif
    }

    /// <summary>
    /// Reads P0's group exactly the way the production snapshot extractor
    /// does (<c>GetParameterGroup</c> in RevitFamilySnapshotExtractor), so
    /// the probe measures what the content hasher would see.
    /// </summary>
    private static string ReadParamGroup(Document familyDoc, out string id)
    {
        var p = familyDoc.FamilyManager.GetParameters()
            .FirstOrDefault(x => string.Equals(x.Definition?.Name, ParamName, StringComparison.Ordinal));
        if (p is null)
        {
            id = "<missing>";
            return "<missing>";
        }
        id = p.Id.ToString();
#if REVIT2024_OR_GREATER
        return p.Definition?.GetGroupTypeId()?.TypeId ?? "<null>";
#else
        return p.Definition?.ParameterGroup.ToString() ?? "<null>";
#endif
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }
}
