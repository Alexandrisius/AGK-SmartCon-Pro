using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

public sealed class SystemCategoryPlacementTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Floor_Placed_InstanceHasTargetType()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var floorType = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault();
            if (floorType is null) { Skip.Test("В шаблоне нет FloorType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Floors, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var start = XYZ.Zero;
            var end = new XYZ(RevitUnitsCompat.MetersToInternal(1.0), 0, level.Elevation);
            var created = handler!(doc, txService, floorType!, level, start, end);

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created!.GetTypeId()).IsEqualTo(floorType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Roof_Placed_InstanceHasTargetType()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var roofType = new FilteredElementCollector(doc)
                .OfClass(typeof(RoofType))
                .Cast<RoofType>()
                .FirstOrDefault();
            if (roofType is null) { Skip.Test("В шаблоне нет RoofType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Roofs, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var start = XYZ.Zero;
            var end = new XYZ(RevitUnitsCompat.MetersToInternal(1.0), 0, level.Elevation);
            var created = handler!(doc, txService, roofType!, level, start, end);

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<ExtrusionRoof>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(roofType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

#if REVIT2022_OR_GREATER
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Ceiling_Placed_InstanceHasTargetType()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var ceilingType = new FilteredElementCollector(doc)
                .OfClass(typeof(CeilingType))
                .Cast<CeilingType>()
                .FirstOrDefault();
            if (ceilingType is null) { Skip.Test("В шаблоне нет CeilingType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Ceilings, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var start = XYZ.Zero;
            var end = new XYZ(RevitUnitsCompat.MetersToInternal(1.0), 0, level.Elevation);
            var created = handler!(doc, txService, ceilingType!, level, start, end);

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Ceiling>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(ceilingType!.Id);
            }
        }
        finally { doc.Close(false); }
    }
#else
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public Task Ceiling_Placed_SkippedBelowRevit2022()
    {
        Skip.Test("Ceiling.Create доступен только с Revit 2022 — версионный гейт блокирует импорт потолков на этой версии");
        return Task.CompletedTask;
    }
#endif

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PipeInsulation_Placed_HostCreatedAndLinked()
    {
        var doc = NewMepTemplateDocument();
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами изоляции (см. PROBE T)"); }
        try
        {
            var insulationType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipeInsulationType))
                .Cast<Autodesk.Revit.DB.Plumbing.PipeInsulationType>()
                .FirstOrDefault();
            if (insulationType is null) { Skip.Test("В шаблоне нет PipeInsulationType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_PipeInsulations, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, insulationType!, level, XYZ.Zero, new XYZ(3.280839895, 0, 0));

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Plumbing.PipeInsulation>();

                var hostId = ((Autodesk.Revit.DB.Plumbing.PipeInsulation)created!).HostElementId;
                var host = doc.GetElement(hostId);
                await Assert.That(host).IsTypeOf<Autodesk.Revit.DB.Plumbing.Pipe>();

                var insulationIds = InsulationLiningBase.GetInsulationIds(doc, hostId);
                await Assert.That(insulationIds).Contains(created!.Id);
                await Assert.That(created.GetTypeId()).IsEqualTo(insulationType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task DuctInsulation_Placed_HostCreatedAndLinked()
    {
        var doc = NewMepTemplateDocument();
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами изоляции (см. PROBE T)"); }
        try
        {
            var insulationType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Mechanical.DuctInsulationType))
                .Cast<Autodesk.Revit.DB.Mechanical.DuctInsulationType>()
                .FirstOrDefault();
            if (insulationType is null) { Skip.Test("В шаблоне нет DuctInsulationType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_DuctInsulations, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, insulationType!, level, XYZ.Zero, new XYZ(3.280839895, 0, 0));

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Mechanical.DuctInsulation>();

                var hostId = ((Autodesk.Revit.DB.Mechanical.DuctInsulation)created!).HostElementId;
                var host = doc.GetElement(hostId);
                await Assert.That(host).IsTypeOf<Autodesk.Revit.DB.Mechanical.Duct>();

                var insulationIds = InsulationLiningBase.GetInsulationIds(doc, hostId);
                await Assert.That(insulationIds).Contains(created!.Id);
                await Assert.That(created.GetTypeId()).IsEqualTo(insulationType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Stairs_Placed_TargetTypeRunPresentDefaultRailingsRemoved()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var stairsType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Architecture.StairsType))
                .Cast<Autodesk.Revit.DB.Architecture.StairsType>()
                .FirstOrDefault();
            if (stairsType is null) { Skip.Test("В шаблоне нет StairsType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Stairs, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, stairsType!, level, XYZ.Zero, new XYZ(3.280839895, 0, 0));

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Architecture.Stairs>();

                var stairs = (Autodesk.Revit.DB.Architecture.Stairs)created!;
                // ChangeTypeId переключил дефолтный тип на целевой.
                await Assert.That(stairs.GetTypeId()).IsEqualTo(stairsType!.Id);
                // Марш создан (лестница не пустая).
                await Assert.That(stairs.GetStairsRuns().Count).IsGreaterThan(0);
                // Дефолтные ограждения StairsEditScope удалены — эталон чистый.
                await Assert.That(stairs.GetAssociatedRailings().Count).IsEqualTo(0);
            }
        }
        finally { doc.Close(false); }
    }

#if REVIT2025_OR_GREATER
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Railing_Placed_InstanceHasTargetType()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var railingType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Architecture.RailingType))
                .Cast<Autodesk.Revit.DB.Architecture.RailingType>()
                .FirstOrDefault();
            if (railingType is null) { Skip.Test("В шаблоне нет RailingType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_StairsRailing, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, railingType!, level, XYZ.Zero, new XYZ(3.280839895, 0, 0));

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Architecture.Railing>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(railingType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Railing_AnalyzeActiveProject_FindsPlacedInstanceAndType()
    {
        // #182: railing instances/types live in OST_StairsRailing, not
        // OST_Railings — with the wrong constant the picker rejected every
        // railing and AnalyzeActiveProject never saw the category.
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var railingType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Architecture.RailingType))
                .Cast<Autodesk.Revit.DB.Architecture.RailingType>()
                .FirstOrDefault();
            if (railingType is null) { Skip.Test("В шаблоне нет RailingType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_StairsRailing, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, railingType!, level, XYZ.Zero, new XYZ(3.280839895, 0, 0));
            if (created is null) { Skip.Test("Не удалось разместить ограждение в шаблоне"); }

            var ops = new SystemFamilyRevitOperations(
                null!, txService, new LoadableFamilyScanner(),
                new RevitMiniProjectMarker(txService, new SmartCon.Core.Services.Interfaces.SystemClock()));
            var analyses = ops.AnalyzeActiveProject(doc);

            var railingCategory = analyses.FirstOrDefault(
                a => a.Category == BuiltInCategory.OST_StairsRailing);
            using (Assert.Multiple())
            {
                await Assert.That(railingCategory).IsNotNull();
                await Assert.That(railingCategory!.Types.Select(t => t.Name)).Contains(railingType!.Name);
            }
        }
        finally { doc.Close(false); }
    }
#else
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public Task Railing_Placed_SkippedBelowRevit2025()
    {
        Skip.Test("Railing.Create(CurveLoop) доступен только с Revit 2025 — версионный гейт блокирует импорт ограждений на этой версии");
        return Task.CompletedTask;
    }
#endif

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Gate_CeilingsAndRailings_BlockedBelowRequiredVersions()
    {
        using (Assert.Multiple())
        {
            await Assert.That(SystemCategoryPlacementAvailability.IsSupported(BuiltInCategory.OST_Ceilings, 2021)).IsFalse();
            await Assert.That(SystemCategoryPlacementAvailability.IsSupported(BuiltInCategory.OST_Ceilings, 2022)).IsTrue();
            await Assert.That(SystemCategoryPlacementAvailability.IsSupported(BuiltInCategory.OST_StairsRailing, 2024)).IsFalse();
            await Assert.That(SystemCategoryPlacementAvailability.IsSupported(BuiltInCategory.OST_StairsRailing, 2025)).IsTrue();
            await Assert.That(SystemCategoryPlacementAvailability.IsSupported(BuiltInCategory.OST_Floors, 2019)).IsTrue();
            await Assert.That(SystemCategoryPlacementAvailability.IsSupported(BuiltInCategory.OST_PipeInsulations, 2019)).IsTrue();

            // Реестр применяет ту же матрицу.
            await Assert.That(SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Ceilings, 2021)).IsNull();
            await Assert.That(SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_StairsRailing, 2024)).IsNull();
            await Assert.That(SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Floors, 2019)).IsNotNull();
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Extraction_FromPlacedFloor_TypeFoundViaInstances()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var floorType = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault();
            if (floorType is null) { Skip.Test("В шаблоне нет FloorType"); }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Floors, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, floorType!, level, XYZ.Zero, new XYZ(3.280839895, 0, 0));
            await Assert.That(created).IsNotNull();

            // Snapshot extractor обязан найти тип из размещённого инстанса
            // (первая ветка ExtractSystemCategoryFromStagedProject), а не через
            // fallback «все типы категории»: в документе ровно ОДИН инстанс
            // одного типа → первая ветка даёт ровно один тип; fallback вернул
            // бы ВСЕ FloorType шаблона (их несколько).
            var extractor = new RevitFamilySnapshotExtractor();
            var snapshot = extractor.ExtractSystemCategoryFromStagedProject(doc, BuiltInCategory.OST_Floors);

            using (Assert.Multiple())
            {
                await Assert.That(snapshot.Types.Count).IsEqualTo(1);
                await Assert.That(snapshot.Types[0].Name).IsEqualTo(floorType!.Name);
            }
        }
        finally { doc.Close(false); }
    }

    /// <summary>
    /// MEP-шаблон с типами изоляции (CF-4720: пустой шаблон их не содержит).
    /// Реализация переехала в <see cref="Support.SampleFiles.NewMepTemplateDocument"/>
    /// — переиспользуется InsulationHostExclusionTests (#181).
    /// </summary>
    private static Document? NewMepTemplateDocument()
        => Support.SampleFiles.NewMepTemplateDocument(Application);
}

