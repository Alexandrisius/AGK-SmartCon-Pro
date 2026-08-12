using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// E2 (#209, ADR-066): contract probes for the shared-nested dependency
/// collector. Seeds a 3-level family chain (Parent → Child → Grandchild,
/// both nested ones marked Shared via OwnerFamily) and verifies the
/// assumptions the E2 design relies on:
/// P1 — every shared nested family is visible FLAT in the top parent's
/// family document (no depth traversal, no cycle guard needed);
/// P2 — <c>Document.EditFamily</c> works on a FAMILY document for a nested
/// family and the returned document is an independent copy that can be
/// held open and SaveAs'd to managed storage in Phase 3;
/// P3 — the nested document survives the parent document's Close (Phase 3
/// stages the parent first, the nested rows after).
/// </summary>
public sealed class SharedNestedCollectorTests : RevitApiTest
{
    private const string GrandchildName = "SmartConProbeGrandchild";
    private const string ChildName = "SmartConProbeChild";

    private string? _tempDir;
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

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConNested_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);

        var grandchildPath = Path.Combine(_tempDir, GrandchildName + ".rfa");
        var childPath = Path.Combine(_tempDir, ChildName + ".rfa");

        CreateSharedFamily(template, grandchildPath);
        CreateSharedFamily(template, childPath, grandchildPath);

        _parentDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(ParentDoc, "Load child"))
        {
            tx.Start();
            if (!ParentDoc.LoadFamily(childPath, out _))
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
    public async Task P1_SharedNested_AllLevelsVisibleFlatInTopParent()
    {
        var entries = ScanFamilies(ParentDoc);
        SmartConLogger.Info("PROBE P1: " + string.Join(" | ", entries));

        using (Assert.Multiple())
        {
            await Assert.That(entries.Any(e => e.StartsWith(ChildName + ":", StringComparison.Ordinal)))
                .IsTrue();
            await Assert.That(entries.Any(e => e.StartsWith(GrandchildName + ":", StringComparison.Ordinal)))
                .IsTrue();
            await Assert.That(entries.Any(e =>
                    e.StartsWith(GrandchildName + ":", StringComparison.Ordinal) &&
#if NET8_0_OR_GREATER
                    e.Contains("shared=1", StringComparison.Ordinal)))
#else
                    e.IndexOf("shared=1", StringComparison.Ordinal) >= 0))
#endif
                .IsTrue();
        }
    }

    [Test]
    public async Task P2_EditFamily_FromFamilyDocument_NestedDocSaveAsStandalone()
    {
        var nested = FindFamily(ParentDoc, ChildName);
        await Assert.That(nested).IsNotNull();

        var nestedDoc = ParentDoc.EditFamily(nested!);
        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(nestedDoc.IsFamilyDocument).IsTrue();
                await Assert.That(nestedDoc.FamilyManager.Types.Size).IsGreaterThanOrEqualTo(1);
            }

            var standalonePath = Path.Combine(_tempDir!, ChildName + "-standalone.rfa");
            nestedDoc.SaveAs(standalonePath, new SaveAsOptions { OverwriteExistingFile = true });
            await Assert.That(File.Exists(standalonePath)).IsTrue();
        }
        finally
        {
            nestedDoc.Close(false);
        }

        await Assert.That(ParentDoc.IsValidObject).IsTrue();
        SmartConLogger.Info("PROBE P2: EditFamily from family doc + SaveAs standalone succeeded");
    }

    [Test]
    public async Task P2b_EditFamily_GrandchildFromTopParent_Works()
    {
        var nested = FindFamily(ParentDoc, GrandchildName);
        if (nested is null)
        {
            SmartConLogger.Info("PROBE P2b: grandchild not visible in top parent — P1 contract broken");
            await Assert.That(nested).IsNotNull();
            return;
        }

        var nestedDoc = ParentDoc.EditFamily(nested);
        try
        {
            await Assert.That(nestedDoc.IsFamilyDocument).IsTrue();
        }
        finally
        {
            nestedDoc.Close(false);
        }
    }

    [Test]
    public async Task P3_NestedDoc_SurvivesParentClose_AndSaveAsWorks()
    {
        // E2 design contract: Phase 3 stages the parent FIRST (SaveAs +
        // CloseAndRelease closes the parent family doc) and the nested rows
        // AFTER — the held-open nested document must stay valid and
        // SaveAs-able after the parent document is closed (API remarks:
        // EditFamily creates an independent copy of the family).
        var probeParent = Application.NewFamilyDocument(
            SampleFiles.FindFamilyTemplate(Application)
            ?? throw new InvalidOperationException("template missing — seed would have skipped"));
        using (var tx = new Transaction(probeParent, "Load child"))
        {
            tx.Start();
            if (!probeParent.LoadFamily(Path.Combine(_tempDir!, ChildName + ".rfa"), out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            tx.Commit();
        }

        var nestedDoc = probeParent.EditFamily(FindFamily(probeParent, ChildName)!);
        probeParent.Close(false);

        using (Assert.Multiple())
        {
            await Assert.That(nestedDoc.IsValidObject).IsTrue();
            await Assert.That(nestedDoc.IsFamilyDocument).IsTrue();
        }

        var path = Path.Combine(_tempDir!, ChildName + "-after-parent-close.rfa");
        nestedDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        nestedDoc.Close(false);

        await Assert.That(File.Exists(path)).IsTrue();
        SmartConLogger.Info("PROBE P3: nested doc survived parent Close + SaveAs succeeded");
    }

    [Test]
    public async Task Collector_FindsAllSharedNestedFlat_WithSharedNestedKind()
    {
        // Contract test over the real collector (probes P1/P2 above proved
        // the underlying Revit behavior): both nesting levels appear in one
        // flat scan, template built-in families (Section/Level Heads) are
        // excluded as non-shared.
        var sut = new RevitFamilyDependencyCollector();

        var descriptors = sut.CollectSharedNestedDependencies(ParentDoc);

        using (Assert.Multiple())
        {
            await Assert.That(descriptors.Count).IsEqualTo(2);
            await Assert.That(descriptors.Any(d =>
                string.Equals(d.FamilyName, ChildName, StringComparison.Ordinal))).IsTrue();
            await Assert.That(descriptors.Any(d =>
                string.Equals(d.FamilyName, GrandchildName, StringComparison.Ordinal))).IsTrue();
            await Assert.That(descriptors.All(d =>
                d.Kind == FamilyDependencyKind.SharedNested && d.PartName is null)).IsTrue();
            await Assert.That(descriptors.All(d => !string.IsNullOrEmpty(d.FamilyUniqueId))).IsTrue();
        }
    }

    private static Family? FindFamily(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
    }

    private static List<string> ScanFamilies(Document doc)
    {
        var entries = new List<string>();
        foreach (var family in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
        {
            string shared;
            try
            {
                var p = family.get_Parameter(BuiltInParameter.FAMILY_SHARED);
                shared = p is null ? "shared=<null>" : $"shared={p.AsInteger()}";
            }
            catch (Exception ex)
            {
                shared = $"shared=<{ex.GetType().Name}>";
            }

            entries.Add($"{family.Name}:{shared},editable={family.IsEditable},uid={family.UniqueId}");
        }
        return entries;
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
                SmartConLogger.Info(
                    $"PROBE seed: OwnerFamily FAMILY_SHARED for '{Path.GetFileName(targetPath)}': " +
                    $"{(sharedParam is null ? "<null>" : sharedParam.IsReadOnly ? "read-only" : "rw")}");
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
