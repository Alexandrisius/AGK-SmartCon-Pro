using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// #209 synthetic update matrix (2026-08-11) — the machine-independent
/// counterpart of <c>NestedReloadPokeContractTests</c> (which pins the same
/// contracts on the owner's real library). Every scenario seeds a SHARED
/// child family v1 from a template, nests it into an open host family
/// document, mutates the file to v2 ON THE SAME PATH, runs the production
/// update (<c>RevitFamilyLoadService.ReloadFamilyPreservingLoadedTypesAsync</c>
/// — the poke + doc-to-doc path) and asserts:
///   success + FHV8V(embedded in host) == FHV8V(resolved v2 file).
/// FHV8V (<c>FamilyContentHasher.ComputeForEmbeddedVerification</c>) excludes
/// parameter groups and regen-driven metrics (volumes/bounds/curve lengths),
/// so a landed merge verifies even though the host drives the embedded
/// definition. Identity FHV9 (<c>ComputeForLoadable</c>) is asserted to
/// CHANGE where the diff is real — proving the scenario actually mutated
/// the file.
/// </summary>
public sealed class NestedUpdateMatrixTests : RevitApiTest
{
    private const string ChildName = "SmartConMatrixChild";
    private const string BoltName = "SmartConMatrixBolt";
    private const string AssemblyName = "SmartConMatrixAssembly";

    private string? _tempDir;
    private string? _template;
    private string? _childPath;
    private Document? _hostDoc;
    private List<Document>? _openDocs;

    private enum Baseline
    {
        Typed,
        Phantom,
        WithInstanceParam,
        Geometry,
    }

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the update matrix");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConMatrix_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");
        _openDocs = new List<Document>();
        SmartConLogger.Info($"Matrix seed: template='{_template}'");
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
    public async Task Matrix_ParameterGroupOnly_SucceedsAndVerifies()
    {
        // v1↔v2 differ ONLY by the parameter group (P0 re-added under a
        // different group): FHV9 identity changes (groups included there),
        // FHV8V excludes groups, so the update succeeds and verifies.
        CreateChildV1(Baseline.Typed);
        SeedHostWithChild();
        var identityV1 = FileLoadableHash(_childPath!);

        MutateChild("group-only", static doc =>
        {
            RemoveFamilyParameter(doc, "P0");
            AddNumberParameterInDataGroup(doc, "P0");
        });

        var identityV2 = FileLoadableHash(_childPath!);
        SmartConLogger.Info($"Matrix group-only: identity {identityV1[..8]}→{identityV2[..8]}");

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info(
            $"Matrix group-only: success={result.Success} status={result.Status}, " +
            $"embedded={embedded[..8]} file={file[..8]} equal={embedded == file}");

        await Assert.That(identityV2).IsNotEqualTo(identityV1);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_FormulaChanged_LandsAndVerifies()
    {
        CreateChildV1(Baseline.Typed);
        SeedHostWithChild();
        MutateChild("formula", static doc =>
        {
            var p = FindFamilyParameter(doc, "P0");
            doc.FamilyManager.SetFormula(p, "42");
        });

        await Assert.That(EmbeddedVerifyHash(_hostDoc!, ChildName))
            .IsNotEqualTo(FileVerifyHash(_childPath!));

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info($"Matrix formula: success={result.Success}, embedded={embedded[..8]} file={file[..8]}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_ParameterAdded_LandsAndVerifies()
    {
        CreateChildV1(Baseline.Typed);
        SeedHostWithChild();
        MutateChild("add-param", static doc => AddNumberParameter(doc, "P2", isInstance: false));

        await Assert.That(EmbeddedVerifyHash(_hostDoc!, ChildName))
            .IsNotEqualTo(FileVerifyHash(_childPath!));

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info($"Matrix add-param: success={result.Success}, embedded={embedded[..8]} file={file[..8]}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_ParameterRemoved_LandsAndVerifies()
    {
        CreateChildV1(Baseline.Typed);
        SeedHostWithChild();
        MutateChild("remove-param", static doc => RemoveFamilyParameter(doc, "P1"));

        await Assert.That(EmbeddedVerifyHash(_hostDoc!, ChildName))
            .IsNotEqualTo(FileVerifyHash(_childPath!));

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info($"Matrix remove-param: success={result.Success}, embedded={embedded[..8]} file={file[..8]}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_GeometrySizeOnly_MetricsExcluded_Verifies()
    {
        // Same topology (one box), different depth: volumes/bounds change,
        // FHV9 identity changes, FHV8V ignores the metrics — the update
        // succeeds and verifies.
        CreateChildV1(Baseline.Geometry);
        SeedHostWithChild();
        var identityV1 = FileLoadableHash(_childPath!);

        MutateChild("geometry-size", static doc =>
        {
            DeleteFirstExtrusion(doc);
            CreateBox(doc, depth: 2.0, xOffset: 0.0);
        });

        var identityV2 = FileLoadableHash(_childPath!);
        SmartConLogger.Info($"Matrix geometry-size: identity {identityV1[..8]}→{identityV2[..8]}");

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info($"Matrix geometry-size: success={result.Success}, embedded={embedded[..8]} file={file[..8]}");

        await Assert.That(identityV2).IsNotEqualTo(identityV1);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_TopologyChange_LandsAndVerifies()
    {
        // A SECOND box: face/edge counts change — a verification-grade diff.
        CreateChildV1(Baseline.Geometry);
        SeedHostWithChild();
        MutateChild("topology", static doc => CreateBox(doc, depth: 1.0, xOffset: 2.0));

        await Assert.That(EmbeddedVerifyHash(_hostDoc!, ChildName))
            .IsNotEqualTo(FileVerifyHash(_childPath!));

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info($"Matrix topology: success={result.Success}, embedded={embedded[..8]} file={file[..8]}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_PhantomType_LandsAndVerifies()
    {
        // No named types at all (phantom type only) — a real catalog case.
        CreateChildV1(Baseline.Phantom);
        SeedHostWithChild();
        MutateChild("phantom-add-param", static doc => AddNumberParameter(doc, "P2", isInstance: false));

        await Assert.That(EmbeddedVerifyHash(_hostDoc!, ChildName))
            .IsNotEqualTo(FileVerifyHash(_childPath!));

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info($"Matrix phantom: success={result.Success}, embedded={embedded[..8]} file={file[..8]}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_TypeValuesChanged_LandsAndVerifies()
    {
        CreateChildV1(Baseline.Typed);
        SeedHostWithChild();
        MutateChild("type-values", static doc => SetTypeParameterValue(doc, "P0", 20.0));

        await Assert.That(EmbeddedVerifyHash(_hostDoc!, ChildName))
            .IsNotEqualTo(FileVerifyHash(_childPath!));

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        SmartConLogger.Info($"Matrix type-values: success={result.Success}, embedded={embedded[..8]} file={file[..8]}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    [Test]
    public async Task Matrix_InUseInstanceAndBinding_SurviveMerge()
    {
        // The child is IN USE: an instance is placed in the host and its
        // instance parameter is associated to a host family parameter.
        // After the merge the instance and the association must survive.
        CreateChildV1(Baseline.WithInstanceParam);
        SeedHostWithChild();

        var symbolId = FindNestedFamily(_hostDoc!, ChildName).GetFamilySymbolIds().First();
        var symbol = (FamilySymbol)_hostDoc!.GetElement(symbolId);
        FamilyInstance? instance;
        using (var tx = new Transaction(_hostDoc, "Place + associate"))
        {
            tx.Start();
            if (!symbol.IsActive)
            {
                symbol.Activate();
            }
            instance = _hostDoc.FamilyCreate.NewFamilyInstance(
                XYZ.Zero, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            AddNumberParameter(_hostDoc, "HostP", isInstance: true);
            var hostParam = FindFamilyParameter(_hostDoc, "HostP");
            var instanceParam = instance.LookupParameter("PI");
            _hostDoc.FamilyManager.AssociateElementParameterToFamilyParameter(instanceParam, hostParam);
            tx.Commit();
        }

        var instanceId = instance!.Id;
        await Assert.That(
                _hostDoc.FamilyManager.GetAssociatedFamilyParameter(instance.LookupParameter("PI")))
            .IsNotNull();

        MutateChild("in-use-add-param", static doc => AddNumberParameter(doc, "P2", isInstance: false));

        var result = await UpdateChildInHostAsync();
        var embedded = EmbeddedVerifyHash(_hostDoc!, ChildName);
        var file = FileVerifyHash(_childPath!);
        var survived = _hostDoc.GetElement(instanceId) as FamilyInstance;
        SmartConLogger.Info(
            $"Matrix in-use: success={result.Success}, embedded={embedded[..8]} file={file[..8]}, " +
            $"instance survived={survived is not null}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
        await Assert.That(survived).IsNotNull();
        await Assert.That(
                _hostDoc.FamilyManager.GetAssociatedFamilyParameter(survived!.LookupParameter("PI")))
            .IsNotNull();
    }

    [Test]
    public async Task Matrix_Depth2NonSharedIntermediate_HoistedDefinitionUpdates()
    {
        // Bolt (shared) inside a NON-SHARED assembly inside the open top
        // family: the production reload targets the hoisted bolt definition
        // in the TOP document (the only definition reachable from projects —
        // contract of NestedFamilyReloadTests) and must verify.
        var boltPath = Path.Combine(_tempDir!, BoltName + ".rfa");
        var assemblyPath = Path.Combine(_tempDir!, AssemblyName + ".rfa");

        var boltDoc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(boltDoc, "bolt v1"))
        {
            tx.Start();
            SetShared(boltDoc);
            AddNumberParameter(boltDoc, "P0", isInstance: false);
            boltDoc.FamilyManager.NewType("TypeA");
            tx.Commit();
        }
        boltDoc.SaveAs(boltPath, new SaveAsOptions { OverwriteExistingFile = true });
        boltDoc.Close(false);

        var assemblyDoc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(assemblyDoc, "assembly"))
        {
            tx.Start();
            if (!assemblyDoc.LoadFamily(boltPath, out _))
            {
                throw new InvalidOperationException("LoadFamily(bolt) returned false");
            }
            tx.Commit();
        }
        assemblyDoc.SaveAs(assemblyPath, new SaveAsOptions { OverwriteExistingFile = true });
        assemblyDoc.Close(false);

        SeedHostWithAssembly(assemblyPath);

        var boltDocV2 = Application.OpenDocumentFile(boltPath);
        using (var tx = new Transaction(boltDocV2, "bolt v2"))
        {
            tx.Start();
            AddNumberParameter(boltDocV2, "P2", isInstance: false);
            tx.Commit();
        }
        boltDocV2.Save();
        boltDocV2.Close(false);

        await Assert.That(EmbeddedVerifyHash(_hostDoc!, BoltName))
            .IsNotEqualTo(FileVerifyHash(boltPath));

        var result = await CreateService(_hostDoc!).ReloadFamilyPreservingLoadedTypesAsync(
            new FamilyResolvedFile(boltPath, null, null),
            overwriteParameterValues: true);

        var embedded = EmbeddedVerifyHash(_hostDoc!, BoltName);
        var file = FileVerifyHash(boltPath);
        SmartConLogger.Info(
            $"Matrix depth-2: success={result.Success} status={result.Status}, " +
            $"embedded={embedded[..8]} file={file[..8]} equal={embedded == file}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embedded).IsEqualTo(file);
    }

    private void CreateChildV1(Baseline baseline)
    {
        var doc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(doc, "v1"))
        {
            tx.Start();
            SetShared(doc);
            switch (baseline)
            {
                case Baseline.Typed:
                    AddNumberParameter(doc, "P0", isInstance: false);
                    AddNumberParameter(doc, "P1", isInstance: false);
                    doc.FamilyManager.NewType("TypeA");
                    SetTypeParameterValue(doc, "P0", 10.0);
                    break;
                case Baseline.Phantom:
                    AddNumberParameter(doc, "P0", isInstance: false);
                    break;
                case Baseline.WithInstanceParam:
                    AddNumberParameter(doc, "P0", isInstance: false);
                    AddNumberParameter(doc, "PI", isInstance: true);
                    doc.FamilyManager.NewType("TypeA");
                    break;
                case Baseline.Geometry:
                    AddNumberParameter(doc, "P0", isInstance: false);
                    doc.FamilyManager.NewType("TypeA");
                    CreateBox(doc, depth: 1.0, xOffset: 0.0);
                    break;
            }
            tx.Commit();
        }
        doc.SaveAs(_childPath!, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
    }

    private void SeedHostWithChild()
    {
        _hostDoc = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(_hostDoc);
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

    private void SeedHostWithAssembly(string assemblyPath)
    {
        _hostDoc = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(_hostDoc);
        using (var tx = new Transaction(_hostDoc, "Load assembly"))
        {
            tx.Start();
            if (!_hostDoc.LoadFamily(assemblyPath, out _))
            {
                throw new InvalidOperationException("LoadFamily(assembly) returned false");
            }
            tx.Commit();
        }
    }

    private void MutateChild(string txName, Action<Document> mutation)
    {
        var doc = Application.OpenDocumentFile(_childPath!);
        try
        {
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                mutation(doc);
                tx.Commit();
            }
            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    private async Task<FamilyLoadResult> UpdateChildInHostAsync()
    {
        return await CreateService(_hostDoc!).ReloadFamilyPreservingLoadedTypesAsync(
            new FamilyResolvedFile(_childPath!, null, null),
            overwriteParameterValues: true);
    }

    private static RevitFamilyLoadService CreateService(Document hostDoc)
    {
        var context = new StubRevitContext(hostDoc);
        return new RevitFamilyLoadService(context, new RevitTransactionService(context));
    }

    private static void SetShared(Document doc)
    {
        doc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
    }

    private static void AddNumberParameter(Document doc, string name, bool isInstance)
    {
#if REVIT2022_OR_GREATER
        doc.FamilyManager.AddParameter(name, GroupTypeId.General, SpecTypeId.Number, isInstance);
#else
        doc.FamilyManager.AddParameter(name, BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, isInstance);
#endif
    }

    private static void AddNumberParameterInDataGroup(Document doc, string name)
    {
#if REVIT2022_OR_GREATER
        doc.FamilyManager.AddParameter(name, GroupTypeId.Data, SpecTypeId.Number, false);
#else
        doc.FamilyManager.AddParameter(name, BuiltInParameterGroup.PG_DATA, ParameterType.Number, false);
#endif
    }

    private static FamilyParameter FindFamilyParameter(Document doc, string name)
    {
        return doc.FamilyManager.GetParameters()
            .First(p => string.Equals(p.Definition.Name, name, StringComparison.Ordinal));
    }

    private static void RemoveFamilyParameter(Document doc, string name)
    {
        doc.FamilyManager.RemoveParameter(FindFamilyParameter(doc, name));
    }

    private static void SetTypeParameterValue(Document doc, string name, double value)
    {
        doc.FamilyManager.Set(FindFamilyParameter(doc, name), value);
    }

    private static void CreateBox(Document doc, double depth, double xOffset)
    {
        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
        var sketch = SketchPlane.Create(doc, plane);
        var p0 = new XYZ(xOffset, 0, 0);
        var p1 = new XYZ(xOffset + 1, 0, 0);
        var p2 = new XYZ(xOffset + 1, 1, 0);
        var p3 = new XYZ(xOffset, 1, 0);
        var loop = new CurveArray();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));
        var profile = new CurveArrArray();
        profile.Append(loop);
        doc.FamilyCreate.NewExtrusion(true, profile, sketch, depth);
    }

    private static void DeleteFirstExtrusion(Document doc)
    {
        var extrusion = new FilteredElementCollector(doc)
            .OfClass(typeof(Extrusion))
            .First();
        doc.Delete(extrusion.Id);
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }

    private static string EmbeddedVerifyHash(Document hostDoc, string familyName)
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nested = FindNestedFamily(hostDoc, familyName);
        var copy = hostDoc.EditFamily(nested);
        try
        {
            return hasher.ComputeForEmbeddedVerification(extractor.ExtractFromFamilyDocument(copy))!.HexString;
        }
        finally
        {
            copy.Close(false);
        }
    }

    private string FileVerifyHash(string path)
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var doc = Application.OpenDocumentFile(path);
        try
        {
            return hasher.ComputeForEmbeddedVerification(extractor.ExtractFromFamilyDocument(doc))!.HexString;
        }
        finally
        {
            doc.Close(false);
        }
    }

    private string FileLoadableHash(string path)
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var doc = Application.OpenDocumentFile(path);
        try
        {
            return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(doc))!.HexString;
        }
        finally
        {
            doc.Close(false);
        }
    }
}
