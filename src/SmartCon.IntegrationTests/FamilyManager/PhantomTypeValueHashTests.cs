using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// FHV9 contracts (#209 stress test 2026-08-12): parameter VALUES of a
/// typeless family (phantom default type) participate in the identity hash.
/// FHV8 skipped the phantom entirely — an edit of any non-geometric value
/// (e.g. the built-in «Модель») never shifted the hash and the import
/// dialog reported a false Duplicate. The extractor reads phantom values
/// from the unnamed current type (EditFamily/editor context) or synthesizes
/// a temporary Transaction+RollBack type (raw-open context, Size=0) — these
/// contracts pin that both contexts produce the SAME identity hash, that a
/// value edit shifts it, and that the verification grade ignores phantom
/// values (host-driven associations make them non-comparable).
/// Seeding note: a phantom WITH a value is produced the same way the user's
/// editor does it — an EditFamily copy carries the unnamed current type,
/// setting a value on it and SaveAs persists the phantom (raw reopen then
/// reports Types.Size=1, exactly like the owner's editor-saved nut).
/// </summary>
public sealed class PhantomTypeValueHashTests : RevitApiTest
{
    private const string ChildName = "SmartConPhantomValue";

    private string? _tempDir;
    private string? _template;
    private string? _valuePath;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the phantom value contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConPhantomVal_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _openDocs = new List<Document>();

        // Typeless family with one text parameter (no types at all).
        var seedPath = Path.Combine(_tempDir, ChildName + ".rfa");
        var doc = Application.NewFamilyDocument(_template);
        using (var tx = new Transaction(doc, "seed"))
        {
            tx.Start();
#if REVIT2022_OR_GREATER
            doc.FamilyManager.AddParameter("PText", GroupTypeId.General, SpecTypeId.String.Text, false);
#else
            doc.FamilyManager.AddParameter("PText", BuiltInParameterGroup.PG_GENERAL, ParameterType.Text, false);
#endif
            doc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            tx.Commit();
        }
        doc.SaveAs(seedPath, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);

        // Persist a phantom WITH a value: load into a host, edit the copy
        // (the phantom type exists there), set the value, SaveAs — the
        // saved file keeps the phantom on raw reopen (Size=1).
        var host = Application.NewFamilyDocument(_template);
        using (var tx = new Transaction(host, "load child"))
        {
            tx.Start();
            if (!host.LoadFamily(seedPath, out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            tx.Commit();
        }
        var nested = FindNestedFamily(host, ChildName);
        var copy = host.EditFamily(nested);
        _valuePath = Path.Combine(_tempDir, ChildName + "Value.rfa");
        using (var tx = new Transaction(copy, "set phantom value"))
        {
            tx.Start();
            var param = copy.FamilyManager.GetParameters()
                .First(p => string.Equals(p.Definition?.Name, "PText", StringComparison.Ordinal));
            copy.FamilyManager.Set(param, "Hello");
            tx.Commit();
        }
        copy.SaveAs(_valuePath, new SaveAsOptions { OverwriteExistingFile = true });
        copy.Close(false);
        host.Close(false);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        try
        {
            if (_openDocs is not null)
            {
                foreach (var doc in _openDocs)
                {
                    try
                    {
                        if (doc.IsValidObject)
                        {
                            doc.Close(false);
                        }
                    }
                    catch
                    {
                    }
                }
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
    public async Task PhantomValue_IdentityHash_EqualAcrossRawAndEditFamilyContexts()
    {
        // THE cross-context invariant: the raw file (phantom persisted,
        // Size=1) and the EditFamily copy of the same content nested in a
        // host must hash identically — otherwise dedup/verification equality
        // breaks (the reason FHV8 dropped phantom values at all).
        var raw = Application.OpenDocumentFile(_valuePath!);
        _openDocs!.Add(raw);
        SmartConLogger.Info(
            $"PhantomContract: raw open Types.Size={raw.FamilyManager.Types.Size}, " +
            $"CurrentType={(raw.FamilyManager.CurrentType is null ? "null" : "present")}");

        var rawHash = IdentityHash(raw);
        var rawVerify = VerifyHash(raw);

        var host = Application.NewFamilyDocument(_template!);
        _openDocs.Add(host);
        using (var tx = new Transaction(host, "load value child"))
        {
            tx.Start();
            if (!host.LoadFamily(_valuePath!, out _))
            {
                throw new InvalidOperationException("LoadFamily(value child) returned false");
            }
            tx.Commit();
        }
        var copy = host.EditFamily(FindNestedFamily(host, ChildName + "Value"));
        string copyHash;
        string copyVerify;
        try
        {
            copyHash = IdentityHash(copy);
            copyVerify = VerifyHash(copy);
        }
        finally
        {
            copy.Close(false);
        }

        SmartConLogger.Info(
            $"PhantomContract: raw={rawHash[..8]} copy={copyHash[..8]} equal={rawHash == copyHash}; " +
            $"verify raw={rawVerify[..8]} copy={copyVerify[..8]} equal={rawVerify == copyVerify}");

        await Assert.That(rawHash).IsEqualTo(copyHash);
        await Assert.That(rawVerify).IsEqualTo(copyVerify);
    }

    [Test]
    public async Task PhantomValue_ValueEdit_ShiftsIdentityHash()
    {
        // The owner's scenario: edit a text value on a typeless family →
        // the identity hash MUST change (dedup reports "content changed",
        // a new version can be imported).
        var raw = Application.OpenDocumentFile(_valuePath!);
        _openDocs!.Add(raw);
        var before = IdentityHash(raw);

        using (var tx = new Transaction(raw, "edit value"))
        {
            tx.Start();
            var param = raw.FamilyManager.GetParameters()
                .First(p => string.Equals(p.Definition?.Name, "PText", StringComparison.Ordinal));
            raw.FamilyManager.Set(param, "World");
            tx.Commit();
        }
        raw.Save();
        var after = IdentityHash(raw);

        SmartConLogger.Info($"PhantomContract: value edit identity {before[..8]}→{after[..8]} changed={before != after}");
        await Assert.That(after).IsNotEqualTo(before);
    }

    [Test]
    public async Task PhantomValue_ValueEdit_ShiftsVerificationHash()
    {
        // FHV10 unified hash (2026-08-12): phantom values are content —
        // the embedded document keeps the authored phantom values even
        // under a host drive (associations live on instances in the host;
        // DrivenEmbeddedPollutionProbeTests), so a phantom diff is a REAL
        // content diff in every context.
        var raw = Application.OpenDocumentFile(_valuePath!);
        _openDocs!.Add(raw);
        var before = VerifyHash(raw);

        using (var tx = new Transaction(raw, "edit value"))
        {
            tx.Start();
            var param = raw.FamilyManager.GetParameters()
                .First(p => string.Equals(p.Definition?.Name, "PText", StringComparison.Ordinal));
            raw.FamilyManager.Set(param, "World");
            tx.Commit();
        }
        var after = VerifyHash(raw);

        SmartConLogger.Info($"PhantomContract: value edit verify {before[..8]}→{after[..8]} changed={before != after}");
        await Assert.That(after).IsNotEqualTo(before);
    }

    private static string IdentityHash(Document doc)
    {
        return new FamilyContentHasher()
            .ComputeForLoadable(new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc))!
            .HexString;
    }

    private static string VerifyHash(Document doc)
    {
        return new FamilyContentHasher()
            .ComputeForLoadable(new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc))!
            .HexString;
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }
}
