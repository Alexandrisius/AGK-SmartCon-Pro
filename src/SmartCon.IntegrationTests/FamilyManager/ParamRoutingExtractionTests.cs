using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;
using Electrical = Autodesk.Revit.DB.Electrical;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// FHV19 (ADR-072, #254): parameter-based routing extraction. Manager-less
/// MEPCurve types (flex pipe/duct, conduit, cable tray — probe-verified
/// <c>RoutingPreferenceManager == null</c>) expose their fitting selection
/// as ROUTING rules with <c>"Param:&lt;BIP&gt;"</c> group keys; pipe/duct
/// keep the manager-based extraction byte-identical. Uses the DEFAULT
/// template types — no seeding needed (probe RoutingStorageReality matrix).
/// </summary>
public sealed class ParamRoutingExtractionTests : RevitApiTest
{
    private Document? _doc;
    private RevitFamilySnapshotExtractor? _extractor;

    private Document Doc => _doc!;
    private RevitFamilySnapshotExtractor Extractor => _extractor!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocument()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Metric);
        _extractor = new RevitFamilySnapshotExtractor();
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task FlexDuct_RoutingExtractedAsParamRules()
    {
        var type = FindFirst(FlexDuctClass());
        if (type is null) { Skip.Test("В шаблоне нет FlexDuctType"); return; }

        var snapshot = Extractor.ExtractSingleSystemType(Doc, type.Id);

        await Assert.That(snapshot.Routing).IsNotNull();
        var keys = snapshot.Routing!.Rules.Select(r => r.GroupKey!).OrderBy(k => k, StringComparer.Ordinal).ToList();
        await Assert.That(keys).IsEquivalentTo(new[]
        {
            "Param:RBS_CURVETYPE_DEFAULT_TAKEOFF_PARAM",
            "Param:RBS_CURVETYPE_DEFAULT_TEE_PARAM",
            "Param:RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM",
            "Param:RBS_CURVETYPE_DEFAULT_UNION_PARAM",
            "Param:RBS_CURVETYPE_MULTISHAPE_TRANSITION_OVALROUND_PARAM",
            "Param:RBS_CURVETYPE_MULTISHAPE_TRANSITION_PARAM",
            "Param:RBS_CURVETYPE_MULTISHAPE_TRANSITION_RECTOVAL_PARAM",
        }.OrderBy(k => k, StringComparer.Ordinal));
        await Assert.That(snapshot.Routing.Rules.All(r => r.GroupType == RoutingGroupKeys.ParamGroupType)).IsTrue();
        // Template defaults: every fitting row is «Нет» (no-part rule).
        await Assert.That(snapshot.Routing.Rules.All(r => r.PartName is null)).IsTrue();
        // PREFERRED_BRANCH maps to PreferredJunctionType (template: Tee=1).
        await Assert.That(snapshot.Routing.PreferredJunctionType).IsEqualTo(1);
    }

    [Test]
    public async Task Conduit_ParamRulesWithoutPreferredBranch()
    {
        var type = FindFirst(ConduitClass());
        if (type is null) { Skip.Test("В шаблоне нет ConduitType"); return; }

        var snapshot = Extractor.ExtractSingleSystemType(Doc, type.Id);

        await Assert.That(snapshot.Routing).IsNotNull();
        var keys = snapshot.Routing!.Rules.Select(r => r.GroupKey!).ToHashSet(StringComparer.Ordinal);
        await Assert.That(keys.SetEquals(new[]
        {
            "Param:RBS_CURVETYPE_DEFAULT_BEND_PARAM",
            "Param:RBS_CURVETYPE_DEFAULT_CROSS_PARAM",
            "Param:RBS_CURVETYPE_DEFAULT_TEE_PARAM",
            "Param:RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM",
            "Param:RBS_CURVETYPE_DEFAULT_UNION_PARAM",
        })).IsTrue();
        // PREFERRED_BRANCH is hidden on conduit — deterministic 0.
        await Assert.That(snapshot.Routing.PreferredJunctionType).IsEqualTo(0);
    }

    [Test]
    public async Task CableTray_VerticalAndHorizontalBendsAsParamRules()
    {
        var type = FindFirst(CableTrayClass());
        if (type is null) { Skip.Test("В шаблоне нет CableTrayType"); return; }

        var snapshot = Extractor.ExtractSingleSystemType(Doc, type.Id);

        await Assert.That(snapshot.Routing).IsNotNull();
        var keys = snapshot.Routing!.Rules.Select(r => r.GroupKey!).ToList();
        await Assert.That(keys).Contains("Param:RBS_CURVETYPE_DEFAULT_HORIZONTAL_BEND_PARAM");
        await Assert.That(keys).Contains("Param:RBS_CURVETYPE_DEFAULT_ELBOWUP_PARAM");
        await Assert.That(keys).Contains("Param:RBS_CURVETYPE_DEFAULT_ELBOWDOWN_PARAM");
    }

    [Test]
    public async Task PipeType_ManagerRulesKeepIntTokens_NoParamKeys()
    {
        var type = FindFirst(PipeClass());
        if (type is null) { Skip.Test("В шаблоне нет PipeType"); return; }

        var snapshot = Extractor.ExtractSingleSystemType(Doc, type.Id);

        await Assert.That(snapshot.Routing).IsNotNull();
        await Assert.That(snapshot.Routing!.Rules.All(r => r.GroupType >= 0)).IsTrue();
        await Assert.That(snapshot.Routing.Rules.All(r => r.GroupKey is null)).IsTrue();
    }

    [Test]
    public async Task FlexType_RoutingParamsExcludedFromValues()
    {
        var type = FindFirst(FlexDuctClass());
        if (type is null) { Skip.Test("В шаблоне нет FlexDuctType"); return; }

        var snapshot = Extractor.ExtractSingleSystemType(Doc, type.Id);
        var reference = new FilteredElementCollector(Doc).OfClass(FlexDuctClass()).Cast<ElementType>().First();

        // Every visible routing-driving parameter of the type appears as a
        // rule and is gone from VALUES (the bip-based exclusion is
        // locale-invariant, unlike a name-based check).
        var visibleRoutingParams = 0;
        foreach (Parameter p in reference.Parameters)
        {
            if (RoutingDrivingParameters.TryGetRoutingParam(p) is not null
                || RoutingDrivingParameters.IsPreferredBranch(p))
            {
                visibleRoutingParams++;
            }
        }
        await Assert.That(visibleRoutingParams).IsGreaterThan(0);
        await Assert.That(snapshot.Routing!.Rules.Count).IsEqualTo(visibleRoutingParams - 1); // minus PREFERRED_BRANCH (not a rule)
    }

    private ElementType? FindFirst(Type classType)
        => new FilteredElementCollector(Doc).OfClass(classType).Cast<ElementType>().FirstOrDefault();

    private static Type FlexDuctClass() => typeof(FlexDuctType);
    private static Type ConduitClass() => typeof(Electrical.ConduitType);
    private static Type CableTrayClass() => typeof(Electrical.CableTrayType);
    private static Type PipeClass() => typeof(Autodesk.Revit.DB.Plumbing.PipeType);
}
