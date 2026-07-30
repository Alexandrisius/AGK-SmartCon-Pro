using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Stale;

/// <summary>
/// Issue #84 / Phase 24 (ADR-030): the pure helper that re-syncs the
/// <c>SmartCon_FamilyVersion_v1</c> ES marker on every loadable family
/// that just landed in the catalog via "Импорт активного файла" /
/// "Импорт выделенных". No Revit API in the SUT — tests use a hand-written
/// recording fake (see <see cref="revit-mocking"/> skill: Moq cannot
/// proxy interfaces whose method body throws from a constructor).
/// </summary>
public sealed class LoadableMarkerLogicTests
{
    private static FamilyBatchImportItem MakeLoadableItem(
        string catalogItemId,
        string fileName,
        string? versionLabel) =>
        new FamilyBatchImportItem(
            FilePath: $"loadable://{fileName}",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            FamilySource: "loadable",
            PrecomputedCatalogItemId: catalogItemId,
            PrecomputedVersionLabel: versionLabel);

    private static LoadableFamilyAttributeTask MakeTask(string catalogItemId) =>
        new LoadableFamilyAttributeTask(
            CatalogItemId: catalogItemId,
            ManagedRfaPath: $"/dummy/path/{catalogItemId}.rfa",
            VersionId: null,
            FileId: null);

    private sealed class RecordingVersionWriter : IFamilyVersionWriter
    {
        public List<(string CatalogItemId, string FamilyName, string? VersionLabel, int TargetRevit)> Calls { get; } = new();

        public Task WriteVersionMarkerAsync(
            string catalogItemId,
            string familyName,
            string? versionLabel,
            int targetRevit,
            CancellationToken ct)
        {
            Calls.Add((catalogItemId, familyName, versionLabel, targetRevit));
            return Task.CompletedTask;
        }

        public Task WriteSystemTypeMarkerAsync(
            string catalogItemId,
            string typeUniqueId,
            string? versionLabel,
            int targetRevit,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingVersionWriter : IFamilyVersionWriter
    {
        public Exception ToThrow { get; set; } = new InvalidOperationException("simulated Revit failure");

        public Task WriteVersionMarkerAsync(
            string catalogItemId,
            string familyName,
            string? versionLabel,
            int targetRevit,
            CancellationToken ct)
        {
            throw ToThrow;
        }

        public Task WriteSystemTypeMarkerAsync(
            string catalogItemId,
            string typeUniqueId,
            string? versionLabel,
            int targetRevit,
            CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task EmptyTasks_ReturnsZeroSummary()
    {
        var writer = new RecordingVersionWriter();
        var items = new List<FamilyBatchImportItem>();
        var tasks = Array.Empty<LoadableFamilyAttributeTask>();

        var summary = await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            items, tasks, writer, targetRevit: 2025, ct: default);

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, summary.SuccessCount);
        Assert.Equal(0, summary.SkippedCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task SingleLoadable_CallsWriterOnceWithCorrectArgs()
    {
        var writer = new RecordingVersionWriter();
        var item = MakeLoadableItem("cat-1", "FamilyA", "v1");
        var task = MakeTask("cat-1");

        var summary = await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            new List<FamilyBatchImportItem> { item },
            new List<LoadableFamilyAttributeTask> { task },
            writer, targetRevit: 2025, ct: default);

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(0, summary.SkippedCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Single(writer.Calls);
        Assert.Equal("cat-1", writer.Calls[0].CatalogItemId);
        Assert.Equal("FamilyA", writer.Calls[0].FamilyName);
        Assert.Equal("v1", writer.Calls[0].VersionLabel);
        Assert.Equal(2025, writer.Calls[0].TargetRevit);
    }

    [Fact]
    public async Task MultipleLoadables_AllCalledInOrder()
    {
        var writer = new RecordingVersionWriter();
        var items = new List<FamilyBatchImportItem>
        {
            MakeLoadableItem("cat-1", "FamilyA", "v1"),
            MakeLoadableItem("cat-2", "FamilyB", "v2"),
            MakeLoadableItem("cat-3", "FamilyC", "v3"),
        };
        var tasks = new List<LoadableFamilyAttributeTask>
        {
            MakeTask("cat-1"),
            MakeTask("cat-2"),
            MakeTask("cat-3"),
        };

        var summary = await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            items, tasks, writer, targetRevit: 2025, ct: default);

        Assert.Equal(3, summary.Total);
        Assert.Equal(3, summary.SuccessCount);
        Assert.Equal(0, summary.SkippedCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal(3, writer.Calls.Count);
        Assert.Equal("v1", writer.Calls[0].VersionLabel);
        Assert.Equal("v2", writer.Calls[1].VersionLabel);
        Assert.Equal("v3", writer.Calls[2].VersionLabel);
    }

    [Fact]
    public async Task NoMatchingBatchItem_Skipped()
    {
        // The orchestrator returns tasks for families that succeeded in the
        // catalog write, but the VM-side batch list might not contain the
        // matching item (e.g. it was filtered out by the user in the
        // dialog). We must not crash; just skip and count.
        var writer = new RecordingVersionWriter();
        var item = MakeLoadableItem("cat-1", "FamilyA", "v1");
        var task = MakeTask("cat-other");

        var summary = await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            new List<FamilyBatchImportItem> { item },
            new List<LoadableFamilyAttributeTask> { task },
            writer, targetRevit: 2025, ct: default);

        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.SuccessCount);
        Assert.Equal(1, summary.SkippedCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task WriterThrows_FailedCountedAndOtherFamiliesStillProcessed()
    {
        // Per the issue: marker write failure for a single family is logged
        // with [Action: ...] and counted, but MUST NOT abort the rest of the
        // batch — the catalog import has already succeeded.
        var writer = new ThrowingVersionWriter
        {
            ToThrow = new InvalidOperationException("simulated Revit failure")
        };
        var items = new List<FamilyBatchImportItem>
        {
            MakeLoadableItem("cat-1", "FamilyA", "v1"),
            MakeLoadableItem("cat-2", "FamilyB", "v2"),
        };
        var tasks = new List<LoadableFamilyAttributeTask>
        {
            MakeTask("cat-1"),
            MakeTask("cat-2"),
        };

        var summary = await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            items, tasks, writer, targetRevit: 2025, ct: default);

        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.SuccessCount);
        Assert.Equal(0, summary.SkippedCount);
        Assert.Equal(2, summary.FailedCount);
    }

    [Fact]
    public async Task WriterThrowsForOneFamilyButNotOthers_FailureIsIsolated()
    {
        // Mixed batch: family 1 succeeds, family 2 fails, family 3 succeeds.
        // Verifies the try/catch isolates per-family failures (the bug that
        // motivated moving the logic into a Core helper with unit tests).
        var writer = new FlakyVersionWriter(
            failForCatalogItemIds: new HashSet<string>(StringComparer.Ordinal) { "cat-2" });
        var items = new List<FamilyBatchImportItem>
        {
            MakeLoadableItem("cat-1", "FamilyA", "v1"),
            MakeLoadableItem("cat-2", "FamilyB", "v2"),
            MakeLoadableItem("cat-3", "FamilyC", "v3"),
        };
        var tasks = new List<LoadableFamilyAttributeTask>
        {
            MakeTask("cat-1"),
            MakeTask("cat-2"),
            MakeTask("cat-3"),
        };

        var summary = await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            items, tasks, writer, targetRevit: 2025, ct: default);

        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.SuccessCount);
        Assert.Equal(0, summary.SkippedCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(3, writer.Calls.Count);
        Assert.Contains(writer.Calls, c => c.CatalogItemId == "cat-1");
        Assert.Contains(writer.Calls, c => c.CatalogItemId == "cat-3");
    }

    [Fact]
    public async Task TargetRevit_ForwardedToWriterVerbatim()
    {
        // The VM resolves the Revit version at call time (CurrentRevitVersion);
        // the helper must pass it through unchanged. We pin a non-default
        // value (2024) to catch any accidental hard-coding.
        var writer = new RecordingVersionWriter();
        var item = MakeLoadableItem("cat-1", "FamilyA", "v1");
        var task = MakeTask("cat-1");

        var summary = await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            new List<FamilyBatchImportItem> { item },
            new List<LoadableFamilyAttributeTask> { task },
            writer, targetRevit: 2024, ct: default);

        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(2024, writer.Calls[0].TargetRevit);
    }

    [Fact]
    public async Task NullArgs_Throw()
    {
        var writer = new RecordingVersionWriter();
        var items = new List<FamilyBatchImportItem>();
        var tasks = Array.Empty<LoadableFamilyAttributeTask>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
                null!, tasks, writer, targetRevit: 2025, ct: default));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
                items, null!, writer, targetRevit: 2025, ct: default));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
                items, tasks, null!, targetRevit: 2025, ct: default));
    }

    private sealed class FlakyVersionWriter : IFamilyVersionWriter
    {
        private readonly HashSet<string> _failForCatalogItemIds;

        public List<(string CatalogItemId, string FamilyName, string? VersionLabel, int TargetRevit)> Calls { get; } = new();

        public FlakyVersionWriter(HashSet<string> failForCatalogItemIds)
        {
            _failForCatalogItemIds = failForCatalogItemIds;
        }

        public Task WriteVersionMarkerAsync(
            string catalogItemId,
            string familyName,
            string? versionLabel,
            int targetRevit,
            CancellationToken ct)
        {
            Calls.Add((catalogItemId, familyName, versionLabel, targetRevit));
            if (_failForCatalogItemIds.Contains(catalogItemId))
            {
                throw new InvalidOperationException($"simulated failure for {catalogItemId}");
            }
            return Task.CompletedTask;
        }

        public Task WriteSystemTypeMarkerAsync(
            string catalogItemId,
            string typeUniqueId,
            string? versionLabel,
            int targetRevit,
            CancellationToken ct) => Task.CompletedTask;
    }
}
