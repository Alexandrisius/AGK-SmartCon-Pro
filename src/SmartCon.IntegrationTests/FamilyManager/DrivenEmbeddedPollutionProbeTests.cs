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
/// CONTRACT (probe 2026-08-12): the EditFamily DOCUMENT of a nested family
/// is NOT polluted when the host drives it — the host drives through
/// INSTANCE-parameter associations that live in the host document (the
/// only channel a shared nested family supports; type-param association of
/// a shared nested is impossible even in the UI), while the embedded
/// definition keeps the authored state. Measured: baseline FHV10(embedded)
/// == FHV10(file) for a static family; under an active 999-value drive the
/// embedded document still shows the authored values and FHV10 stays equal.
/// This is the empirical foundation of the unified FHV10 hash: it
/// includes metrics/phantom/connector sizes — only parameter groups are
/// excluded (they never propagate on merge — GroupPropagationProbeTests).
/// </summary>
public sealed class DrivenEmbeddedPollutionProbeTests : RevitApiTest
{
    private const string ChildName = "SmartConDrivenChild";

    private string? _tempDir;
    private string? _childPath;
    private Document? _hostDoc;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the driven-pollution probe");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConDriven_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");

        // Child: shared, type param P0=5, instance param PI, TypeA, one box.
        var doc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(doc, "v1"))
        {
            tx.Start();
            doc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            AddNumberParameter(doc, "P0", isInstance: false);
            AddNumberParameter(doc, "PI", isInstance: true);
            doc.FamilyManager.NewType("TypeA");
            doc.FamilyManager.CurrentType = doc.FamilyManager.Types.Cast<FamilyType>()
                .First(t => string.Equals(t.Name, "TypeA", StringComparison.Ordinal));
            doc.FamilyManager.Set(FindFamilyParameter(doc, "P0"), 5.0);
            CreateBox(doc, depth: 1.0);
            tx.Commit();
        }
        doc.SaveAs(_childPath!, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);

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
    public async Task Probe_DrivenInstanceParam_WhatLandsInEmbeddedDocument()
    {
        // --- Baseline: no drive — full identity hash embedded vs file ---
        var baseEmbedded = MeasureEmbedded(_hostDoc!, out var baseSnap);
        var baseFile = MeasureFile(_childPath!, out var fileSnap);
        SmartConLogger.Info(
            $"PROBE_DRIVEN: baseline (no association) FHV10 embedded==file: {baseEmbedded == baseFile}");
        SmartConLogger.Info(
            $"PROBE_DRIVEN: baseline embedded P0={DescribeValue(baseSnap, "P0")} PI={DescribeValue(baseSnap, "PI")} " +
            $"file P0={DescribeValue(fileSnap, "P0")} PI={DescribeValue(fileSnap, "PI")}, " +
            $"embedded vol={DescribeVolume(baseSnap)} file vol={DescribeVolume(fileSnap)}");

        // --- Drive: place an instance, associate its INSTANCE parameter PI
        // to a host parameter HostP := 999 (the only association channel
        // shared nested families support — type-param association of a
        // shared nested is impossible even in the UI), regenerate ---
        var symbolId = FindNestedFamily(_hostDoc!, ChildName).GetFamilySymbolIds().First();
        ElementId instanceId;
        using (var tx = new Transaction(_hostDoc!, "Place + associate + drive"))
        {
            tx.Start();
            var symbol = (FamilySymbol)_hostDoc!.GetElement(symbolId);
            if (!symbol.IsActive)
            {
                symbol.Activate();
            }
            var instance = _hostDoc.FamilyCreate.NewFamilyInstance(
                XYZ.Zero, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            AddNumberParameter(_hostDoc, "HostP", isInstance: true);
            _hostDoc.FamilyManager.NewType("HostT");
            _hostDoc.FamilyManager.CurrentType = _hostDoc.FamilyManager.Types.Cast<FamilyType>()
                .First(t => string.Equals(t.Name, "HostT", StringComparison.Ordinal));
            var hostParam = FindFamilyParameter(_hostDoc, "HostP");
            _hostDoc.FamilyManager.Set(hostParam, 999.0);
            var instanceParam = instance.LookupParameter("PI");
            _hostDoc.FamilyManager.AssociateElementParameterToFamilyParameter(instanceParam, hostParam);
            _hostDoc.Regenerate();
            tx.Commit();
            instanceId = instance.Id;
        }

        // Sanity: the drive really happened — the nested INSTANCE in the
        // host shows 999.
        var drivenInstance = _hostDoc!.GetElement(instanceId) as FamilyInstance;
        var hostView = drivenInstance?.LookupParameter("PI")?.AsDouble() ?? double.NaN;
        SmartConLogger.Info($"PROBE_DRIVEN: host-side nested instance PI after drive = {hostView} (expect 999)");

        // --- Measure the EditFamily DOCUMENT of the nested family ---
        var drivenEmbedded = MeasureEmbedded(_hostDoc!, out var drivenSnap);
        var drivenFile = MeasureFile(_childPath!, out _);
        SmartConLogger.Info(
            $"PROBE_DRIVEN: after drive embedded P0={DescribeValue(drivenSnap, "P0")} PI={DescribeValue(drivenSnap, "PI")} " +
            $"(authored: P0=5, PI unset), embedded vol={DescribeVolume(drivenSnap)} (file vol={DescribeVolume(fileSnap)})");
        SmartConLogger.Info(
            $"PROBE_DRIVEN: after drive FHV10 embedded==file: {drivenEmbedded == drivenFile}; " +
            $"FHV10 embedded==file: {EmbeddedVerifyEqual(_hostDoc!, _childPath!)}");

        var piEmbedded = ReadValueNumber(drivenSnap, "PI");
        SmartConLogger.Info(
            $"PROBE_DRIVEN: VERDICT embedded-document pollution = {(piEmbedded.HasValue && System.Math.Abs(piEmbedded.Value - 999.0) < 1e-9 ? "CONFIRMED (driven value baked into the EditFamily document)" : "NOT CONFIRMED (EditFamily document keeps the authored state)")}");

        // The contract: the drive really happened in the host (999 on the
        // instance), yet the embedded document keeps the authored state —
        // full identity hashes match the file in both grades, before and
        // after the drive.
        await Assert.That(hostView).IsEqualTo(999.0);
        await Assert.That(baseEmbedded).IsEqualTo(baseFile);
        await Assert.That(drivenEmbedded).IsEqualTo(drivenFile);
        await Assert.That(piEmbedded ?? 0.0).IsEqualTo(0.0);
    }

    private static void AddNumberParameter(Document doc, string name, bool isInstance)
    {
#if REVIT2022_OR_GREATER
        doc.FamilyManager.AddParameter(name, GroupTypeId.General, SpecTypeId.Number, isInstance);
#else
        doc.FamilyManager.AddParameter(name, BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, isInstance);
#endif
    }

    private static FamilyParameter FindFamilyParameter(Document doc, string name)
    {
        return doc.FamilyManager.GetParameters()
            .First(p => string.Equals(p.Definition.Name, name, StringComparison.Ordinal));
    }

    private static void CreateBox(Document doc, double depth)
    {
        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
        var sketch = SketchPlane.Create(doc, plane);
        var p0 = new XYZ(0, 0, 0);
        var p1 = new XYZ(1, 0, 0);
        var p2 = new XYZ(1, 1, 0);
        var p3 = new XYZ(0, 1, 0);
        var loop = new CurveArray();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));
        var profile = new CurveArrArray();
        profile.Append(loop);
        doc.FamilyCreate.NewExtrusion(true, profile, sketch, depth);
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Full identity hash (FHV10) of the embedded EditFamily copy.</summary>
    private static string MeasureEmbedded(Document hostDoc, out FamilySnapshot snapshot)
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var nested = FindNestedFamily(hostDoc, ChildName);
        var copy = hostDoc.EditFamily(nested);
        try
        {
            snapshot = extractor.ExtractFromFamilyDocument(copy);
            return new FamilyContentHasher().ComputeForLoadable(snapshot)!.HexString;
        }
        finally
        {
            copy.Close(false);
        }
    }

    /// <summary>Full identity hash (FHV10) of the source file, opened raw.</summary>
    private string MeasureFile(string path, out FamilySnapshot snapshot)
    {
        var doc = Application.OpenDocumentFile(path);
        try
        {
            snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);
            return new FamilyContentHasher().ComputeForLoadable(snapshot)!.HexString;
        }
        finally
        {
            doc.Close(false);
        }
    }

    private bool EmbeddedVerifyEqual(Document hostDoc, string path)
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nested = FindNestedFamily(hostDoc, ChildName);
        var copy = hostDoc.EditFamily(nested);
        string embedded;
        try
        {
            embedded = hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString;
        }
        finally
        {
            copy.Close(false);
        }

        var fileDoc = Application.OpenDocumentFile(path);
        string file;
        try
        {
            file = hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(fileDoc))!.HexString;
        }
        finally
        {
            fileDoc.Close(false);
        }
        return string.Equals(embedded, file, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeValue(FamilySnapshot snap, string paramName)
    {
        var v = ReadValue(snap, paramName);
        if (v is null)
        {
            return "<absent>";
        }
        return v.ValueNumber.HasValue
            ? v.ValueNumber.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : (v.ValueText ?? "<empty>");
    }

    private static double? ReadValueNumber(FamilySnapshot snap, string paramName)
        => ReadValue(snap, paramName)?.ValueNumber;

    private static FamilyParameterValue? ReadValue(FamilySnapshot snap, string paramName)
    {
        foreach (var t in snap.Types)
        {
            foreach (var v in t.Values)
            {
                if (string.Equals(v.ParameterName, paramName, StringComparison.Ordinal))
                {
                    return v;
                }
            }
        }
        if (snap.PhantomTypeValues is not null)
        {
            foreach (var v in snap.PhantomTypeValues)
            {
                if (string.Equals(v.ParameterName, paramName, StringComparison.Ordinal))
                {
                    return v;
                }
            }
        }
        return null;
    }

    private static string DescribeVolume(FamilySnapshot snap)
    {
        var forms = snap.Geometry.Forms;
        return forms.Count == 0
            ? "<no forms>"
            : forms[0].Volume.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }
}
