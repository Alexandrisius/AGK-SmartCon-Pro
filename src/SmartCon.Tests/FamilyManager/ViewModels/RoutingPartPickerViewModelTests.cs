using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// <see cref="RoutingPartPickerViewModel"/> (owner stress test 2026-09-01):
/// баг 1 — the junctions picker filters tee/tap candidates by the type's
/// preferred junction (the Revit routing dialog never applies the
/// non-preferred kind either); баг 3a — the pipe Segments row persists its
/// size-range criterion through the normal criteria path.
/// </summary>
public sealed class RoutingPartPickerViewModelTests
{
    /// <summary>The pipe junctions group's part_type ordinals (tee + both
    /// taps) — read from the catalog descriptor so the matrix stays the
    /// single source of truth.</summary>
    private static IReadOnlyList<int> JunctionOrdinals
        => RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.PipeCurvesCategoryId)
            .First(g => g.GroupKey == "Junctions")
            .PartTypeOrdinals;

    private static RoutingPartCandidate Candidate(string name, int? partTypeOrdinal)
        => new("id-" + name, name, null, [name + "Type"], partTypeOrdinal);

    [Fact]
    public async Task Initialize_PreferredTap_HidesTeeCandidates()
    {
        var service = new StubRoutingEditorService(
        [
            Candidate("TeeFam", 6),        // PartTee — inactive when tap preferred
            Candidate("TapPerpFam", 10),   // PartTapPerpendicular
            Candidate("TapAdjFam", 11),    // PartTapAdjustable
            Candidate("UnknownPartFam", null), // no fact — passes through
        ]);
        var sut = new RoutingPartPickerViewModel(
            service,
            RoutingGroupCatalog.PipeFittingCategoryId,
            JunctionOrdinals,
            initialPartName: null,
            preferredJunctionType: 1); // tap

        await sut.InitializeAsync();

        Assert.DoesNotContain(sut.Candidates, c => c.FamilyName == "TeeFam");
        Assert.Contains(sut.Candidates, c => c.FamilyName == "TapPerpFam");
        Assert.Contains(sut.Candidates, c => c.FamilyName == "TapAdjFam");
        Assert.Contains(sut.Candidates, c => c.FamilyName == "UnknownPartFam");
    }

    [Fact]
    public async Task Initialize_PreferredTee_HidesTapCandidates()
    {
        var service = new StubRoutingEditorService(
        [
            Candidate("TeeFam", 6),
            Candidate("TapPerpFam", 10),
            Candidate("TapAdjFam", 11),
        ]);
        var sut = new RoutingPartPickerViewModel(
            service,
            RoutingGroupCatalog.PipeFittingCategoryId,
            JunctionOrdinals,
            initialPartName: null,
            preferredJunctionType: 0); // tee

        await sut.InitializeAsync();

        Assert.Contains(sut.Candidates, c => c.FamilyName == "TeeFam");
        Assert.DoesNotContain(sut.Candidates, c => c.FamilyName == "TapPerpFam");
        Assert.DoesNotContain(sut.Candidates, c => c.FamilyName == "TapAdjFam");
    }

    [Fact]
    public async Task Initialize_NoJunctionFilter_KeepsEverything()
    {
        var service = new StubRoutingEditorService(
        [
            Candidate("TeeFam", 6),
            Candidate("TapPerpFam", 10),
        ]);
        var sut = new RoutingPartPickerViewModel(
            service,
            RoutingGroupCatalog.PipeFittingCategoryId,
            JunctionOrdinals,
            initialPartName: null); // preferredJunctionType defaults to -1

        await sut.InitializeAsync();

        Assert.Equal(2, sut.Candidates.Count);
    }

    [Fact]
    public void TryToRecords_SegmentRow_IsReadOnly_Skipped()
    {
        // FHV21 (owner decision 2026-09-01): the pipe Segments row is a
        // READ-ONLY view of the active version's per-version segment
        // configuration — it never persists through the editor (the service
        // preserves legacy stored rows verbatim instead). This kills the old
        // silent criterion-loss bug by construction.
        var descriptor = RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.PipeCurvesCategoryId)[0];
        Assert.True(descriptor.IsSegmentRow);
        Assert.True(descriptor.IsReadOnly);
        Assert.False(descriptor.HasCriteria);

        var state = new RoutingTypeEditState(0);
        var group = new RoutingGroupEditState(descriptor);
        group.Rules.Add(new RoutingRuleEditState
        {
            PartName = "Seg A",
            MinSizeText = "50",
            MaxSizeText = "100",
        });
        state.Groups.Add(group);

        var ok = state.TryToRecords(
            new RoutingEditorTypeData("Type A", "Single", "Pipe Types", true),
            out var records, out var validationError);

        Assert.True(ok, validationError);
        Assert.Empty(records);
    }

    private sealed class StubRoutingEditorService : IRoutingEditorService
    {
        private readonly IReadOnlyList<RoutingPartCandidate> _candidates;

        public StubRoutingEditorService(IReadOnlyList<RoutingPartCandidate> candidates)
        {
            _candidates = candidates;
        }

        public Task<RoutingEditorData?> LoadAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<RoutingEditorData?>(null);

        public Task<RoutingSaveResult> SaveAsync(
            string catalogItemId, RoutingEditorSave save, CancellationToken ct = default)
            => Task.FromResult(new RoutingSaveResult(true, [], null));

        public Task<IReadOnlyList<RoutingPartCandidate>> GetPartCandidatesAsync(
            int fittingCategoryId, IReadOnlyCollection<int> partTypeOrdinals,
            int connectorShapeBits = 0, int requiredShapeMask = 0,
            bool excludeMultiShape = false, CancellationToken ct = default)
            => Task.FromResult(_candidates);
    }
}
