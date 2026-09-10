using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Placement coverage for the Phase-1 (legacy) linear categories (audit B5
/// finding: only the 9 Phase-2 categories had placement tests — the 7
/// handlers that existed before ADR-027 Phase 2 were unprotected against
/// registry refactors): pipes, flex pipes, ducts, flex ducts, conduit,
/// cable tray, walls. Every test is self-contained (no generic helpers —
/// a generic method with a Revit-typed type parameter breaks the Nice3point
/// injector's in-Revit discovery, see audit 2026-08-04).
/// </summary>
public sealed class LegacyCategoryPlacementTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Pipe_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами труб"); }
        try
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipeType))
                .Cast<Autodesk.Revit.DB.Plumbing.PipeType>()
                .FirstOrDefault();
            if (pipeType is null) { Skip.Test("В шаблоне нет PipeType"); }

            var handler = GetHandler(BuiltInCategory.OST_PipeCurves);
            if (handler is null) { Skip.Test("Нет handler"); }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, pipeType!, FirstLevel(doc), XYZ.Zero, OneMeterAlongX());

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Plumbing.Pipe>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(pipeType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FlexPipe_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами гибких труб"); }
        try
        {
            var flexPipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Plumbing.FlexPipeType))
                .Cast<Autodesk.Revit.DB.Plumbing.FlexPipeType>()
                .FirstOrDefault();
            if (flexPipeType is null) { Skip.Test("В шаблоне нет FlexPipeType"); }

            var handler = GetHandler(BuiltInCategory.OST_FlexPipeCurves);
            if (handler is null) { Skip.Test("Нет handler"); }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, flexPipeType!, FirstLevel(doc), XYZ.Zero, OneMeterAlongX());

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Plumbing.FlexPipe>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(flexPipeType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Duct_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами воздуховодов"); }
        try
        {
            var ductType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Mechanical.DuctType))
                .Cast<Autodesk.Revit.DB.Mechanical.DuctType>()
                .FirstOrDefault();
            if (ductType is null) { Skip.Test("В шаблоне нет DuctType"); }

            var handler = GetHandler(BuiltInCategory.OST_DuctCurves);
            if (handler is null) { Skip.Test("Нет handler"); }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, ductType!, FirstLevel(doc), XYZ.Zero, OneMeterAlongX());

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Mechanical.Duct>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(ductType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FlexDuct_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами гибких воздуховодов"); }
        try
        {
            var flexDuctType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Mechanical.FlexDuctType))
                .Cast<Autodesk.Revit.DB.Mechanical.FlexDuctType>()
                .FirstOrDefault();
            if (flexDuctType is null) { Skip.Test("В шаблоне нет FlexDuctType"); }

            var handler = GetHandler(BuiltInCategory.OST_FlexDuctCurves);
            if (handler is null) { Skip.Test("Нет handler"); }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, flexDuctType!, FirstLevel(doc), XYZ.Zero, OneMeterAlongX());

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Mechanical.FlexDuct>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(flexDuctType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Conduit_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами коробов"); }
        try
        {
            var conduitType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Electrical.ConduitType))
                .Cast<Autodesk.Revit.DB.Electrical.ConduitType>()
                .FirstOrDefault();
            if (conduitType is null) { Skip.Test("В шаблоне нет ConduitType"); }

            var handler = GetHandler(BuiltInCategory.OST_Conduit);
            if (handler is null) { Skip.Test("Нет handler"); }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, conduitType!, FirstLevel(doc), XYZ.Zero, OneMeterAlongX());

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Electrical.Conduit>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(conduitType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CableTray_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами лотков"); }
        try
        {
            var cableTrayType = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Electrical.CableTrayType))
                .Cast<Autodesk.Revit.DB.Electrical.CableTrayType>()
                .FirstOrDefault();
            if (cableTrayType is null) { Skip.Test("В шаблоне нет CableTrayType"); }

            var handler = GetHandler(BuiltInCategory.OST_CableTray);
            if (handler is null) { Skip.Test("Нет handler"); }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, cableTrayType!, FirstLevel(doc), XYZ.Zero, OneMeterAlongX());

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Autodesk.Revit.DB.Electrical.CableTray>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(cableTrayType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Wall_Placed_InstanceHasTargetType()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var wallType = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(w => w.Kind == WallKind.Basic);
            if (wallType is null) { Skip.Test("В шаблоне нет базового WallType"); }

            var handler = GetHandler(BuiltInCategory.OST_Walls);
            if (handler is null) { Skip.Test("Нет handler"); }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, wallType!, FirstLevel(doc), XYZ.Zero, OneMeterAlongX());

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Wall>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(wallType!.Id);
            }
        }
        finally { doc.Close(false); }
    }

    private static SystemCategoryRegistry.PlacementHandler? GetHandler(BuiltInCategory category)
        => SystemCategoryRegistry.GetPlacementHandler(category, int.Parse(Application.VersionNumber));

    private static Level FirstLevel(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .First();

    private static XYZ OneMeterAlongX()
        => new(RevitUnitsCompat.MetersToInternal(1.0), 0, 0);
}
