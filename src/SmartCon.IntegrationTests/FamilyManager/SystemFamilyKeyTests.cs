using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #190 (ADR-064): the locale-invariant family key — per-category
/// discriminators (IsWithFitting / WallType.Kind / StairsType.ConstructionMethod)
/// and key-first matching in <see cref="RevitSystemTypeFinder"/>.
/// </summary>
public sealed class SystemFamilyKeyTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Resolver_ConduitAndCableTray_DistinguishesFittingFamilies()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var conduitTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType)).Cast<ConduitType>().ToList();
            var withFittings = conduitTypes.Where(t => t.IsWithFitting).ToList();
            var withoutFittings = conduitTypes.Where(t => !t.IsWithFitting).ToList();
            if (withFittings.Count == 0 || withoutFittings.Count == 0)
            {
                Skip.Test("В шаблоне нет обеих conduit-семей (with/without fittings)");
                return;
            }

            foreach (var t in withFittings)
                await Assert.That(SystemFamilyKeyResolver.Resolve(t)).IsEqualTo(SystemFamilyKeys.ConduitWithFittings);
            foreach (var t in withoutFittings)
                await Assert.That(SystemFamilyKeyResolver.Resolve(t)).IsEqualTo(SystemFamilyKeys.ConduitWithoutFittings);
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Resolver_WallAndStairs_Discriminators()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var basicWall = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .FirstOrDefault(t => t.Kind == WallKind.Basic);
            if (basicWall is null) { Skip.Test("В шаблоне нет Basic WallType"); return; }

            await Assert.That(SystemFamilyKeyResolver.Resolve(basicWall))
                .IsEqualTo(SystemFamilyKeys.WallBasic);

            var stairsType = new FilteredElementCollector(doc)
                .OfClass(typeof(StairsType)).Cast<StairsType>()
                .FirstOrDefault();
            if (stairsType is not null)
            {
                var key = SystemFamilyKeyResolver.Resolve(stairsType);
                await Assert.That(
                    key == SystemFamilyKeys.StairsAssembled
                    || key == SystemFamilyKeys.StairsCastInPlace
                    || key == SystemFamilyKeys.StairsPrecast).IsTrue();
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FindTypeByName_FamilyKey_MatchesOnlyThatFamily()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var conduitTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType)).Cast<ConduitType>().ToList();
            var byFamily = conduitTypes.GroupBy(t => t.IsWithFitting).ToList();
            if (byFamily.Count < 2) { Skip.Test("В шаблоне нет обеих conduit-семей"); return; }

            // Pick a type name present in the with-fittings family; the
            // key-first lookup must return a type of EXACTLY that family
            // even if a same-named type exists in the other family.
            var target = byFamily.First(g => g.Key).First();
            var finder = new RevitSystemTypeFinder();
            var ordinal = (int)BuiltInCategory.OST_Conduit;

            var byKey = finder.FindTypeByName(
                doc, target.Name, ordinal, familyKey: SystemFamilyKeys.ConduitWithFittings);
            await Assert.That(byKey).IsNotNull();
            var resolved = (ConduitType)doc.GetElement(byKey!)!;
            await Assert.That(resolved.IsWithFitting).IsTrue();

            var wrongKey = finder.FindTypeByName(
                doc, target.Name + "__no_such_type__", ordinal, familyKey: SystemFamilyKeys.ConduitWithFittings);
            await Assert.That(wrongKey).IsNull();

            // Legacy path (no key, localized family name) still works.
            var byName = finder.FindTypeByName(doc, target.Name, ordinal, familyName: target.FamilyName);
            await Assert.That(byName).IsNotNull();
        }
        finally { doc.Close(false); }
    }
}
