using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// World B (ADR-072) staging completeness: the clean project must port the
/// source's shared project parameters (ADSK_*/BP_* class) — without them a
/// reimport from the mini-project sees a phantom VALUES diff against the
/// version imported from the live project (owner bug report 2026-08-29).
/// </summary>
public sealed class SharedParameterPortStagingTests : RevitApiTest
{
    private const string TestTypeName = "SmartCon Port Test Pipe";
    private const string ProbeParamName = "SC_PortProbe";
    private const string ProbeValue = "port-probe-value";

    private Document? _sourceDoc;
    private Document? _stagingDoc;
    private SystemTypeSyncService? _syncService;
    private ElementId? _sourceTypeId;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _stagingDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var tx = new RevitTransactionService(new StubRevitContext(_stagingDoc));
        var materialSync = new RevitMaterialSyncService();
        _syncService = new SystemTypeSyncService(
            tx, new RevitFamilySnapshotExtractor(), new RevitSystemTypeFinder(), new SystemClock(),
            materialSync, new RevitSegmentSyncService(materialSync), new NullFittingDependencyResolver(),
            new RevitCompoundStructureSyncService(materialSync));

        _sourceTx = new RevitTransactionService(new StubRevitContext(_sourceDoc));
        _sourceTx.RunInTransaction(_sourceDoc, "Seed source type with a shared parameter", doc =>
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();
            if (pipeType is null) return;

            var seed = (ElementType)pipeType.Duplicate(TestTypeName);
            BindSharedProbeParameter(doc, seed);
            _sourceTypeId = seed.Id;
        });

        if (_sourceTypeId is null)
        {
            Skip.Test("В шаблоне проекта нет PipeType — сидирование невозможно");
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _stagingDoc?.Close(false);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task StageType_PortsMissingSharedParameter_WithValue()
    {
        var result = _syncService!.StageTypeFromSource(
            _sourceDoc!, _stagingDoc!, TestTypeName,
            (int)BuiltInCategory.OST_PipeCurves);

        await Assert.That(result.Status.ToString()).IsEqualTo("Created");

        var staged = new FilteredElementCollector(_stagingDoc!)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>()
            .FirstOrDefault(t => string.Equals(t.Name, TestTypeName, System.StringComparison.OrdinalIgnoreCase));
        await Assert.That(staged).IsNotNull();

        var ported = staged!.LookupParameter(ProbeParamName);
        await Assert.That(ported).IsNotNull();
        await Assert.That(ported!.AsString()).IsEqualTo(ProbeValue);

        // The ported definition must be VISIBLE (Element.Parameters), not a
        // LookupParameter-only ghost — a same-name duplicate definition in
        // the source must not leak in as an invisible clone.
        var visible = false;
        foreach (Parameter p in staged.Parameters)
        {
            if (string.Equals(p.Definition?.Name, ProbeParamName, System.StringComparison.Ordinal))
            {
                visible = true;
                break;
            }
        }
        await Assert.That(visible).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task StageType_SecondRun_IsIdempotent()
    {
        _syncService!.StageTypeFromSource(
            _sourceDoc!, _stagingDoc!, TestTypeName,
            (int)BuiltInCategory.OST_PipeCurves);

        var second = _syncService.StageTypeFromSource(
            _sourceDoc!, _stagingDoc!, TestTypeName,
            (int)BuiltInCategory.OST_PipeCurves);

        await Assert.That(second.Status.ToString()).IsEqualTo("Updated");
        var staged = new FilteredElementCollector(_stagingDoc!)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>()
            .FirstOrDefault(t => string.Equals(t.Name, TestTypeName, System.StringComparison.OrdinalIgnoreCase));
        var ported = staged?.LookupParameter(ProbeParamName);
        await Assert.That(ported).IsNotNull();
        await Assert.That(ported!.AsString()).IsEqualTo(ProbeValue);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task StageType_DuplicateNameDoubleParam_PortsVisibleParameter()
    {
        // Owner stress test 2026-08-30 (Трубы): the source carried a visible
        // Double shared parameter PLUS a same-name duplicate bound to the
        // SAME category. The staged type lost the parameter entirely
        // (reimport saw 13 vs 14 params → phantom version).
        const string dupName = "SC_DupProbe";
        _sourceTx!.RunInTransaction(_sourceDoc!, "Seed duplicate-name double parameter", doc =>
        {
            var seed = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .First(t => string.Equals(t.Name, TestTypeName, System.StringComparison.Ordinal));
            BindDoubleParamWithSameNameDuplicate(doc, seed, dupName);
        });

        var result = _syncService!.StageTypeFromSource(
            _sourceDoc!, _stagingDoc!, TestTypeName,
            (int)BuiltInCategory.OST_PipeCurves);
        await Assert.That(result.Status.ToString()).IsEqualTo("Created");

        var staged = new FilteredElementCollector(_stagingDoc!)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>()
            .FirstOrDefault(t => string.Equals(t.Name, TestTypeName, System.StringComparison.OrdinalIgnoreCase));
        await Assert.That(staged).IsNotNull();

        var visibleCount = 0;
        double? visibleValue = null;
        foreach (Parameter p in staged!.Parameters)
        {
            if (string.Equals(p.Definition?.Name, dupName, System.StringComparison.Ordinal))
            {
                visibleCount++;
                visibleValue = p.AsDouble();
            }
        }
        await Assert.That(visibleCount).IsEqualTo(1);
        await Assert.That(visibleValue).IsNotNull();
        await Assert.That(System.Math.Abs(visibleValue!.Value - 8)).IsLessThan(0.001);
    }

    private static void BindDoubleParamWithSameNameDuplicate(Document doc, ElementType target, string name)
    {
        var app = doc.Application;
        var originalFile = app.SharedParametersFilename;
        var tempFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "smartcon-it-dup-" + System.Guid.NewGuid().ToString("N") + ".txt");
        var tempFile2 = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "smartcon-it-dup-" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var pipeCategory = Category.GetCategory(doc, BuiltInCategory.OST_PipeCurves);

            System.IO.File.WriteAllText(tempFile, string.Empty);
            app.SharedParametersFilename = tempFile;
            var file1 = app.OpenSharedParameterFile();
            var group1 = file1!.Groups.Create("SmartConTests");
            var options1 = CreateDoubleOptions(name);
            options1.GUID = System.Guid.NewGuid();
            options1.Visible = true;
            var visibleDef = group1.Definitions.Create(options1);
            var categories1 = app.Create.NewCategorySet();
            categories1.Insert(pipeCategory);
            if (!doc.ParameterBindings.Insert(visibleDef, app.Create.NewTypeBinding(categories1)))
            {
                throw new InvalidOperationException("visible double binding failed");
            }

            var param = target.LookupParameter(name);
            if (param is null || !param.Set(8.0))
            {
                throw new InvalidOperationException("visible double parameter write failed");
            }

            System.IO.File.WriteAllText(tempFile2, string.Empty);
            app.SharedParametersFilename = tempFile2;
            var file2 = app.OpenSharedParameterFile();
            var group2 = file2!.Groups.Create("SmartConTests");
            var options2 = CreateDoubleOptions(name);
            options2.GUID = System.Guid.NewGuid();
            options2.Visible = false;
            var invisibleDef = group2.Definitions.Create(options2);
            var categories2 = app.Create.NewCategorySet();
            categories2.Insert(pipeCategory);
            if (!doc.ParameterBindings.Insert(invisibleDef, app.Create.NewTypeBinding(categories2)))
            {
                throw new InvalidOperationException("invisible duplicate binding failed");
            }
        }
        finally
        {
            try { app.SharedParametersFilename = originalFile; } catch { }
            try { System.IO.File.Delete(tempFile); } catch { }
            try { System.IO.File.Delete(tempFile2); } catch { }
        }
    }

    private static ExternalDefinitionCreationOptions CreateDoubleOptions(string name)
    {
#if REVIT2022_OR_GREATER
        return new ExternalDefinitionCreationOptions(name, SpecTypeId.Number);
#else
#pragma warning disable CS0618
        return new ExternalDefinitionCreationOptions(name, ParameterType.Number);
#pragma warning restore CS0618
#endif
    }

    private static void BindSharedProbeParameter(Document doc, ElementType target)
    {
        var app = doc.Application;
        var originalFile = app.SharedParametersFilename;
        var tempFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "smartcon-it-shared-" + System.Guid.NewGuid().ToString("N") + ".txt");
        var tempFile2 = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "smartcon-it-shared-" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            System.IO.File.WriteAllText(tempFile, string.Empty);
            app.SharedParametersFilename = tempFile;
            var definitionFile = app.OpenSharedParameterFile();
            var group = definitionFile!.Groups.Create("SmartConTests");
            var options = CreateOptions();
            options.GUID = System.Guid.NewGuid();
            var definition = group.Definitions.Create(options);

            var categories = app.Create.NewCategorySet();
            categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeCurves));
            var binding = app.Create.NewTypeBinding(categories);
            if (!doc.ParameterBindings.Insert(definition, binding))
            {
                throw new InvalidOperationException("probe shared parameter binding failed");
            }

            var param = target.LookupParameter(ProbeParamName);
            if (param is null || !param.Set(ProbeValue))
            {
                throw new InvalidOperationException("probe shared parameter write failed");
            }

            // Same-name duplicate with a DIFFERENT GUID bound to a foreign
            // category — the port must follow the parameter's own binding,
            // not the first name match (owner bug 2026-08-30: an invisible
            // duplicate leaked in and produced a phantom VALUES diff). A
            // shared parameter file forbids same-name definitions, so the
            // duplicate comes from a SECOND temp file.
            System.IO.File.WriteAllText(tempFile2, string.Empty);
            app.SharedParametersFilename = tempFile2;
            var duplicateFile = app.OpenSharedParameterFile();
            var duplicateGroup = duplicateFile!.Groups.Create("SmartConTests");
            var duplicateOptions = CreateOptions();
            duplicateOptions.GUID = System.Guid.NewGuid();
            var duplicate = duplicateGroup.Definitions.Create(duplicateOptions);
            var foreignCategories = app.Create.NewCategorySet();
            foreignCategories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Walls));
            var foreignBinding = app.Create.NewTypeBinding(foreignCategories);
            if (!doc.ParameterBindings.Insert(duplicate, foreignBinding))
            {
                throw new InvalidOperationException("duplicate probe binding failed");
            }
        }
        finally
        {
            try { app.SharedParametersFilename = originalFile; } catch { }
            try { System.IO.File.Delete(tempFile); } catch { }
            try { System.IO.File.Delete(tempFile2); } catch { }
        }
    }

    private static ExternalDefinitionCreationOptions CreateOptions()
    {
#if REVIT2022_OR_GREATER
        return new ExternalDefinitionCreationOptions(ProbeParamName, SpecTypeId.String.Text);
#else
#pragma warning disable CS0618
        return new ExternalDefinitionCreationOptions(ProbeParamName, ParameterType.Text);
#pragma warning restore CS0618
#endif
    }

    private RevitTransactionService? _sourceTx;

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }
}
