using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Compound structure sync (Issue #104, Phase 2): слои стен заменяются
/// эталоном из мини-проекта (состав, толщины, материалы, shell-границы),
/// размещённые стены подхватывают изменение типа автоматически.
/// </summary>
public sealed class SystemTypeStructureSyncTests : RevitApiTest
{
    private const string TypeName = "SmartCon Structured Wall";
    private const string CoreMaterialName = "SmartCon Wall Core";
    private const string FinishMaterialName = "SmartCon Wall Finish";

    private static double Mm(double mm) => mm / 304.8;

    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _targetTx;
    private RevitSystemTypeFinder? _finder;
    private SystemTypeSyncService? _syncService;
    private RevitMaterialSyncService? _materialSync;

    private Document SourceDoc => _sourceDoc!;
    private Document TargetDoc => _targetDoc!;
    private SystemTypeSyncService SyncService => _syncService!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        _targetTx = new RevitTransactionService(new StubRevitContext(TargetDoc));
        _finder = new RevitSystemTypeFinder();
        _materialSync = new RevitMaterialSyncService();
        var segmentSync = new RevitSegmentSyncService(_materialSync);
        _syncService = new SystemTypeSyncService(
            _targetTx, new RevitFamilySnapshotExtractor(), _finder, new SystemClock(),
            _materialSync, segmentSync, new NullFittingDependencyResolver(),
            new RevitCompoundStructureSyncService(_materialSync));

        var seeded = false;
        sourceTx.RunInTransaction(SourceDoc, "Seed reference wall type", doc =>
        {
            var wallType = FindBasicWallType(doc);
            if (wallType is null) return;
            var anyMaterial = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault();
            if (anyMaterial is null) return;

            var coreMaterial = anyMaterial.Duplicate(CoreMaterialName);
            coreMaterial.Color = new Color(120, 120, 120);
            var finishMaterial = anyMaterial.Duplicate(FinishMaterialName);
            finishMaterial.Color = new Color(30, 120, 30);

            var type = (WallType)wallType.Duplicate(TypeName);
            var layers = new List<CompoundStructureLayer>
            {
                new(Mm(10), MaterialFunctionAssignment.Finish1, finishMaterial.Id),
                new(Mm(200), MaterialFunctionAssignment.Structure, coreMaterial.Id),
            };
            var structure = CompoundStructure.CreateSimpleCompoundStructure(layers);
            structure.SetNumberOfShellLayers(ShellLayerType.Exterior, 1);
            type.SetCompoundStructure(structure);
            seeded = true;
        });

        if (!seeded)
        {
            Skip.Test("В шаблоне проекта нет слоистого WallType/Material — сидирование невозможно");
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetDoc?.Close(false);
    }

    [Test]
    public async Task Structure_NewType_LayersShellsAndMaterialsApplied()
    {
        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-wall", "v1",
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Created);
            await Assert.That(result.NotConvergedCount).IsEqualTo(0);
        }

        var typeId = _finder!.FindTypeByName(TargetDoc, TypeName, null);
        var wallType = (WallType)TargetDoc.GetElement(typeId!)!;
        using var structure = wallType.GetCompoundStructure();
        using (Assert.Multiple())
        {
            await Assert.That(structure).IsNotNull();
            var layers = structure!.GetLayers();
            await Assert.That(layers.Count).IsEqualTo(2);
            await Assert.That(layers[0].Function).IsEqualTo(MaterialFunctionAssignment.Finish1);
            await Assert.That(layers[0].Width).IsEqualTo(Mm(10));
            await Assert.That(layers[1].Function).IsEqualTo(MaterialFunctionAssignment.Structure);
            await Assert.That(layers[1].Width).IsEqualTo(Mm(200));
            await Assert.That(structure.GetNumberOfShellLayers(ShellLayerType.Exterior)).IsEqualTo(1);
            await Assert.That(structure.GetNumberOfShellLayers(ShellLayerType.Interior)).IsEqualTo(0);

            var finishMaterial = TargetDoc.GetElement(layers[0].MaterialId) as Material;
            var coreMaterial = TargetDoc.GetElement(layers[1].MaterialId) as Material;
            await Assert.That(finishMaterial?.Name).IsEqualTo(FinishMaterialName);
            await Assert.That(coreMaterial?.Name).IsEqualTo(CoreMaterialName);
            await Assert.That(coreMaterial!.Color.Red).IsEqualTo((byte)120);
        }
    }

    [Test]
    public async Task Structure_ExistingType_LayersReplacedAndPlacedWallFollows()
    {
        // В проекте: тип с тем же именем, но одним слоем 100 мм, и
        // размещённая стена этого типа.
        ElementId wallId = null!;
        _targetTx!.RunInTransaction(TargetDoc, "Seed local wall type and instance", doc =>
        {
            var wallType = FindBasicWallType(doc)!;
            var anyMaterial = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .First();

            var local = (WallType)wallType.Duplicate(TypeName);
            var layers = new List<CompoundStructureLayer>
            {
                new(Mm(100), MaterialFunctionAssignment.Structure, anyMaterial.Id),
            };
            local.SetCompoundStructure(CompoundStructure.CreateSimpleCompoundStructure(layers));

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).First();
            var line = Line.CreateBound(XYZ.Zero, new XYZ(5, 0, 0));
            var wall = Wall.Create(doc, line, local.Id, level.Id, Mm(3000), 0, false, false);
            wallId = wall.Id;
        });

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-wall", "v1",
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Updated);
        }

        var typeId = _finder!.FindTypeByName(TargetDoc, TypeName, null);
        var wallType = (WallType)TargetDoc.GetElement(typeId!)!;
        using var structure = wallType.GetCompoundStructure();
        using (Assert.Multiple())
        {
            var layers = structure!.GetLayers();
            await Assert.That(layers.Count).IsEqualTo(2);
            await Assert.That(structure.GetNumberOfShellLayers(ShellLayerType.Exterior)).IsEqualTo(1);

            // Размещённая стена подхватила новую толщину типа (10 + 200 мм).
            var wall = (Wall)TargetDoc.GetElement(wallId)!;
            await Assert.That(wall.Width).IsEqualTo(Mm(210));
        }
    }

    [Test]
    public async Task Structure_MaterialReusedWhenAlreadyInProject_NoDuplicates()
    {
        _targetTx!.RunInTransaction(TargetDoc, "Seed local materials", doc =>
        {
            var anyMaterial = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .First();
            anyMaterial.Duplicate(CoreMaterialName).Color = new Color(1, 2, 3);
        });

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-wall", "v1",
            int.Parse(Application.VersionNumber));

        await Assert.That(result.IsSuccess).IsTrue();

        using (Assert.Multiple())
        {
            var coreCount = new FilteredElementCollector(TargetDoc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .Count(m => string.Equals(m.Name, CoreMaterialName, StringComparison.OrdinalIgnoreCase));
            await Assert.That(coreCount).IsEqualTo(1);

            // Существующий материал обновлён эталоном.
            var core = new FilteredElementCollector(TargetDoc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .First(m => string.Equals(m.Name, CoreMaterialName, StringComparison.OrdinalIgnoreCase));
            await Assert.That(core.Color.Red).IsEqualTo((byte)120);
        }
    }

    [Test]
    public async Task Structure_InvalidReference_ExistingStructureKeptAndReported()
    {
        // Rejection-path: shell-конфигурация несовместима с числом слоёв
        // (ext + int >= count, нет ни одного core-слоя) — существующая
        // структура типа не трогается, расхождение репортится.
        ElementId typeId = null!;
        double originalWidth = 0;
        _targetTx!.RunInTransaction(TargetDoc, "Seed local wall type", doc =>
        {
            var wallType = FindBasicWallType(doc)!;
            var anyMaterial = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .First();
            var local = (WallType)wallType.Duplicate(TypeName);
            var layers = new List<CompoundStructureLayer>
            {
                new(Mm(150), MaterialFunctionAssignment.Structure, anyMaterial.Id),
            };
            local.SetCompoundStructure(CompoundStructure.CreateSimpleCompoundStructure(layers));
            typeId = local.Id;
            originalWidth = Mm(150);
        });

        var invalidReference = new CompoundStructureSnapshot(
            ExteriorShellLayerCount: 1,
            InteriorShellLayerCount: 1,
            Layers:
            [
                new CompoundLayerSnapshot(
                    (int)MaterialFunctionAssignment.Finish1, Mm(10), null, false),
                new CompoundLayerSnapshot(
                    (int)MaterialFunctionAssignment.Structure, Mm(200), null, false),
            ]);

        var structureSync = new RevitCompoundStructureSyncService(_materialSync!);
        var notConverged = 0;
        _targetTx.RunInTransaction(TargetDoc, "Apply invalid structure", doc =>
        {
            var target = (ElementType)doc.GetElement(typeId)!;
            notConverged = structureSync.SyncStructure(SourceDoc, doc, target, invalidReference);
        });

        var wallType = (WallType)TargetDoc.GetElement(typeId)!;
        using var structure = wallType.GetCompoundStructure();
        using (Assert.Multiple())
        {
            await Assert.That(notConverged).IsGreaterThanOrEqualTo(1);
            // Существующая структура не тронута.
            await Assert.That(structure!.GetLayers().Count).IsEqualTo(1);
            await Assert.That(wallType.Width).IsEqualTo(originalWidth);
        }
    }

    private static WallType? FindBasicWallType(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(t =>
            {
                try
                {
                    using var cs = t.GetCompoundStructure();
                    return cs is not null;
                }
                catch
                {
                    return false;
                }
            });
    }

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion) => null;
    }
}
