using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Routing editor edit-state logic (ADR-072, Phase 3): dirty fingerprints,
/// mm↔feet size conversion, record building (Segments carry-through,
/// empty-row skipping, non-primary criteria roundtrip, validation).
/// </summary>
public class RoutingTypeEditStateTests
{
    private static readonly RoutingEditorTypeData Type = new("Type A", "Single", "Pipe Types", true);

    private static RoutingGroupDescriptor ManagerGroup(string key = "Elbows") => new(
        key, 1, "FM_Routing_Group_Elbows",
        IsReadOnly: false, AllowMultipleRules: true, HasCriteria: true,
        RoutingGroupCatalog.PipeFittingCategoryId, [5]);

    private static RoutingGroupDescriptor SegmentsGroup() => new(
        "Segments", 0, "FM_Routing_Group_SegmentsPipe",
        IsReadOnly: true, AllowMultipleRules: false, HasCriteria: false, 0, []);

    private static RoutingGroupDescriptor ParamGroup() => new(
        RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM"), null,
        "FM_Routing_Group_Junctions",
        IsReadOnly: false, AllowMultipleRules: false, HasCriteria: false,
        RoutingGroupCatalog.PipeFittingCategoryId, [6]);

    [Fact]
    public void Fingerprint_ChangesOnAnyEdit()
    {
        var state = new RoutingTypeEditState(0);
        var group = new RoutingGroupEditState(ManagerGroup());
        group.Rules.Add(new RoutingRuleEditState { PartName = "A:B" });
        state.Groups.Add(group);

        var baseline = state.Fingerprint();
        Assert.Equal(baseline, state.Fingerprint());

        state.Groups[0].Rules[0].Description = "x";
        Assert.NotEqual(baseline, state.Fingerprint());

        var withDescription = state.Fingerprint();
        state.Groups[0].Rules[0].MinSizeText = "50";
        Assert.NotEqual(withDescription, state.Fingerprint());

        var withSize = state.Fingerprint();
        state.PreferredJunctionType = 1;
        Assert.NotEqual(withSize, state.Fingerprint());
    }

    [Fact]
    public void TryToRecords_ConvertsMmToFeet_AndOrdersPerGroup()
    {
        var state = new RoutingTypeEditState(0);
        var group = new RoutingGroupEditState(ManagerGroup());
        group.Rules.Add(new RoutingRuleEditState
        {
            PartName = "A:B", Description = "первый", MinSizeText = "50", MaxSizeText = "100",
        });
        group.Rules.Add(new RoutingRuleEditState { PartName = null, Description = "Нет" });
        state.Groups.Add(group);

        Assert.True(state.TryToRecords(Type, out var records, out var error));
        Assert.Null(error);
        Assert.Equal(2, records.Count);

        Assert.Equal(0, records[0].RuleOrder);
        Assert.Equal("A:B", records[0].PartName);
        var criterion = Assert.Single(records[0].Criteria);
        Assert.Equal("PrimarySizeCriterion", criterion.CriterionType);
        Assert.Equal(50.0 / 304.8, criterion.MinimumSize, 6);
        Assert.Equal(100.0 / 304.8, criterion.MaximumSize, 6);

        Assert.Equal(1, records[1].RuleOrder);
        Assert.Null(records[1].PartName);
    }

    [Fact]
    public void TryToRecords_UnboundedSizes_RoundtripTheRevitSentinel()
    {
        // Revit stores an unrestricted PrimarySizeCriterion as 0..10000 ft;
        // the editor shows «Все» and writes the same sentinel back.
        var state = new RoutingTypeEditState(0);
        var group = new RoutingGroupEditState(ManagerGroup());
        group.Rules.Add(new RoutingRuleEditState { PartName = "A:B" });
        state.Groups.Add(group);

        Assert.True(state.TryToRecords(Type, out var records, out _));
        var criterion = Assert.Single(records[0].Criteria);
        Assert.Equal(0, criterion.MinimumSize);
        Assert.Equal(10000, criterion.MaximumSize);
    }

    [Fact]
    public void FromStored_AllSizesSentinel_DisplaysAsAllLabel()
    {
        var stored = new FamilyRoutingRuleInfo("Type A", "Single", "Elbows", 0, "A:B", "",
            [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0, 10000)]);
        var edit = RoutingRuleEditState.FromStored(stored, new HashSet<string>());

        Assert.Equal(RoutingTypeEditState.FormatSize(0, isMax: false), edit.MinSizeText);
        Assert.Equal(RoutingTypeEditState.FormatSize(0, isMax: true), edit.MaxSizeText);
        Assert.NotEqual("", edit.MaxSizeText);
        // Owner UI review 2026-08-30: an unrestricted rule reads «Все» in
        // BOTH columns — an empty min next to «Все» looked broken.
        Assert.Equal(edit.MaxSizeText, edit.MinSizeText);
    }

    [Fact]
    public void TryToRecords_InvalidSize_FailsWithGroupKey()
    {
        var state = new RoutingTypeEditState(0);
        var group = new RoutingGroupEditState(ManagerGroup());
        group.Rules.Add(new RoutingRuleEditState { PartName = "A:B", MinSizeText = "abc" });
        state.Groups.Add(group);

        Assert.False(state.TryToRecords(Type, out _, out var error));
        Assert.Equal("Elbows", error);
    }

    [Fact]
    public void TryToRecords_SkipsEmptyRows_ButKeepsSegmentsAndParamRows()
    {
        var state = new RoutingTypeEditState(0);
        var segments = new RoutingGroupEditState(SegmentsGroup());
        segments.Rules.Add(new RoutingRuleEditState { PartName = "Seg A" });
        var manager = new RoutingGroupEditState(ManagerGroup());
        manager.Rules.Add(new RoutingRuleEditState()); // fully blank row — skipped
        manager.Rules.Add(new RoutingRuleEditState { PartName = "A:B" });
        var param = new RoutingGroupEditState(ParamGroup());
        param.Rules.Add(RoutingRuleEditState.Empty()); // param «Нет» persists
        state.Groups.Add(segments);
        state.Groups.Add(manager);
        state.Groups.Add(param);

        Assert.True(state.TryToRecords(Type, out var records, out _));

        Assert.Equal(3, records.Count);
        Assert.Equal("Segments", records[0].GroupKey);
        Assert.Equal("Seg A", records[0].PartName);
        Assert.Equal("Elbows", records[1].GroupKey);
        Assert.Equal("A:B", records[1].PartName);
        Assert.Equal(ParamGroup().GroupKey, records[2].GroupKey);
        Assert.Null(records[2].PartName);
        Assert.Empty(records[2].Criteria);
    }

    [Fact]
    public void TryToRecords_PreservesNonPrimaryCriteria()
    {
        var state = new RoutingTypeEditState(0);
        var group = new RoutingGroupEditState(ManagerGroup());
        var rule = new RoutingRuleEditState { PartName = "A:B", MinSizeText = "50" };
        rule.OtherCriteria.Add(new RoutingCriterionSnapshot("SecondarySizeCriterion", 0.1, 0.2));
        group.Rules.Add(rule);
        state.Groups.Add(group);

        Assert.True(state.TryToRecords(Type, out var records, out _));
        Assert.Equal(2, records[0].Criteria.Count);
        Assert.Equal("PrimarySizeCriterion", records[0].Criteria[0].CriterionType);
        Assert.Equal("SecondarySizeCriterion", records[0].Criteria[1].CriterionType);
        Assert.Equal(0.2, records[0].Criteria[1].MaximumSize);
    }

    [Fact]
    public void FromStored_RoundtripsThroughTryToRecords()
    {
        var stored = new FamilyRoutingRuleInfo("Type A", "Single", "Elbows", 0, "A:B", "отвод",
            [new RoutingCriterionSnapshot("PrimarySizeCriterion", 50.0 / 304.8, 100.0 / 304.8),
             new RoutingCriterionSnapshot("Other", 1, 2)]);
        var edit = RoutingRuleEditState.FromStored(stored, new HashSet<string>());

        var state = new RoutingTypeEditState(0);
        var group = new RoutingGroupEditState(ManagerGroup());
        group.Rules.Add(edit);
        state.Groups.Add(group);

        Assert.True(state.TryToRecords(Type, out var records, out _));
        var roundtrip = Assert.Single(records);
        Assert.Equal(stored.PartName, roundtrip.PartName);
        Assert.Equal(stored.Description, roundtrip.Description);
        Assert.Equal(stored.Criteria.Count, roundtrip.Criteria.Count);
        Assert.Equal(stored.Criteria[0].MinimumSize, roundtrip.Criteria[0].MinimumSize, 6);
        Assert.Equal(stored.Criteria[0].MaximumSize, roundtrip.Criteria[0].MaximumSize, 6);
        Assert.Equal("Other", roundtrip.Criteria[1].CriterionType);
    }

    [Fact]
    public void FromStored_FlagsMissingPartFamily()
    {
        var stored = new FamilyRoutingRuleInfo("Type A", "Single", "Elbows", 0, "Missing:DN50", "", []);
        var edit = RoutingRuleEditState.FromStored(stored, new HashSet<string> { "Missing" });
        Assert.True(edit.HasPresenceIssue);

        var present = RoutingRuleEditState.FromStored(stored, new HashSet<string>());
        Assert.False(present.HasPresenceIssue);
    }
}
