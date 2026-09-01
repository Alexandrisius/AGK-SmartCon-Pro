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
/// RevitMiniProjectRoutingSlimmingService (ADR-072, Phase 2b): heals a
/// legacy (dirty) staged mini — pre-slim routing extraction (the backfill
/// source), fitting rules cleared (segments kept), orphan materials
/// deleted (the #254 duplicate class), suffixed working copies renamed to
/// the clean base name, saved in place, backups deleted, read-only
/// restored; idempotent AlreadySlim on the second pass.
/// </summary>
public sealed class MiniProjectRoutingSlimmingTests : RevitApiTest
{
    private const string SegmentName = "Slim Probe Segment";
    private const string ScheduleName = "Slim Probe Schedule";
    private const string CleanMaterialName = "Slim Probe Steel";
    private const string SuffixedMaterialName = "Slim Probe Steel1";
    private const string OrphanMaterialName = "Slim Probe Orphan";

    private static double Dn25 => 25.0 / 304.8;

    private Document? _contextDoc;
    private RevitMiniProjectRoutingSlimmingService? _service;
    private string? _tempDir;

    private RevitMiniProjectRoutingSlimmingService Service => _service!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateService()
    {
        _contextDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var transactions = new RevitTransactionService(new StubRevitContext(_contextDoc));
        _service = new RevitMiniProjectRoutingSlimmingService(
            new InlineAwaitableEvent(Application),
            new RevitFamilySnapshotExtractor(),
            transactions);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        _contextDoc?.Close(false);
        _contextDoc = null;
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Temp cleanup is best-effort — a locked file must not fail the suite.
            }
            _tempDir = null;
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task SlimManagedFile_DirtyMini_SlimsAndPreservesReferencedContent()
    {
        var path = CreateDirtyMini(out var seededTypeName);
        if (path is null) { Skip.Test("В шаблоне нет PipeType/Material — сидирование невозможно"); return; }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var outcome = await Service.SlimManagedFileAsync(path);

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(MiniProjectSlimmingStatus.Slimmed);
            await Assert.That(outcome.PreSlimSnapshot).IsNotNull();
            await Assert.That(outcome.OrphanMaterialsDeleted).IsGreaterThanOrEqualTo(1);
            await Assert.That(outcome.MaterialsRenamed).IsGreaterThanOrEqualTo(1);
            await Assert.That(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)).IsTrue();
        }

        // The pre-slim snapshot carries the FULL legacy routing (backfill
        // source): the seeded segment rule + the no-part elbow rule.
        var rules = outcome.PreSlimSnapshot!.Types
            .Where(t => string.Equals(t.Name, seededTypeName, StringComparison.Ordinal))
            .SelectMany(t => t.Routing?.Rules ?? Array.Empty<SmartCon.Core.Models.FamilyManager.RoutingRuleSnapshot>())
            .ToList();
        await Assert.That(rules.Count).IsGreaterThanOrEqualTo(2);

        using (var reopened = Application.OpenDocumentFile(path))
        {
            try
            {
                var type = new FilteredElementCollector(reopened)
                    .OfClass(typeof(PipeType)).Cast<PipeType>()
                    .First(t => string.Equals(t.Name, seededTypeName, StringComparison.Ordinal));
                using (Assert.Multiple())
                {
                    using var manager = type.RoutingPreferenceManager;
                    await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments)).IsEqualTo(1);
                    await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows)).IsEqualTo(0);
                    await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions)).IsEqualTo(0);

                    var orphanGone = !new FilteredElementCollector(reopened)
                        .OfClass(typeof(Material)).Cast<Material>()
                        .Any(m => m.Name == OrphanMaterialName);
                    await Assert.That(orphanGone).IsTrue();

                    // The suffixed working copy was renamed to the clean base
                    // name (the segment still references it).
                    var renamed = new FilteredElementCollector(reopened)
                        .OfClass(typeof(Material)).Cast<Material>()
                        .FirstOrDefault(m => m.Name == CleanMaterialName);
                    await Assert.That(renamed).IsNotNull();
                }
            }
            finally
            {
                reopened.Close(false);
            }
        }

        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "legacy-dirty.*.rvt")
            .Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Assert.That(backups.Count).IsEqualTo(0);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task SlimManagedFile_SecondPass_AlreadySlim()
    {
        var path = CreateDirtyMini(out _);
        if (path is null) { Skip.Test("В шаблоне нет PipeType/Material — сидирование невозможно"); return; }

        var first = await Service.SlimManagedFileAsync(path);
        await Assert.That(first.Status).IsEqualTo(MiniProjectSlimmingStatus.Slimmed);

        var second = await Service.SlimManagedFileAsync(path);
        await Assert.That(second.Status).IsEqualTo(MiniProjectSlimmingStatus.AlreadySlim);
        // No pre-slim snapshot on AlreadySlim — the stored DB rules are
        // protected from being overwritten by the slim state.
        await Assert.That(second.PreSlimSnapshot).IsNull();
    }

    /// <summary>
    /// Builds a legacy-style dirty mini on disk: a pipe type with a segment
    /// (material renamed to the suffixed working-copy name), a no-part
    /// elbow rule, and an unreferenced orphan material — the #254 residue.
    /// </summary>
    private string? CreateDirtyMini(out string typeName)
    {
        const string ProbeTypeName = "Slim Probe Pipe";
        typeName = ProbeTypeName;
        _tempDir = Path.Combine(Path.GetTempPath(), "smartcon-slim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "legacy-dirty.rvt");

        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        var seeded = false;
        try
        {
            var tx = new RevitTransactionService(new StubRevitContext(doc));
            tx.RunInTransaction(doc, "Seed dirty mini", d =>
            {
                var pipeType = new FilteredElementCollector(d)
                    .OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault();
                var material = new FilteredElementCollector(d)
                    .OfClass(typeof(Material)).Cast<Material>().FirstOrDefault();
                if (pipeType is null || material is null) return;

                var segmentMaterial = material.Duplicate(SuffixedMaterialName);
                var schedule = PipeScheduleType.Create(d, ScheduleName);
                var segment = PipeSegment.Create(d, segmentMaterial.Id, schedule.Id,
                    new List<MEPSize> { new(Dn25, Dn25 * 0.9, Dn25, true, true) });
                if (!string.Equals(segment.Name, SegmentName, StringComparison.Ordinal))
                {
                    try { segment.Name = SegmentName; } catch { }
                }
                // The #254 collision pair: the referenced suffixed working
                // copy ('Steel1', above) + two unreferenced orphans — the
                // family-internal base-name record and a stray duplicate.
                material.Duplicate(CleanMaterialName);
                material.Duplicate(OrphanMaterialName);

                var type = (MEPCurveType)pipeType.Duplicate(ProbeTypeName);
                using (var manager = type.RoutingPreferenceManager)
                {
                    for (var i = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) - 1; i >= 0; i--)
                    {
                        manager.RemoveRule(RoutingPreferenceRuleGroupType.Segments, i);
                    }
                    var actualSegment = new FilteredElementCollector(d)
                        .OfClass(typeof(Segment)).Cast<Segment>()
                        .First(s => string.Equals(s.Name, SegmentName, StringComparison.OrdinalIgnoreCase));
                    manager.AddRule(
                        RoutingPreferenceRuleGroupType.Segments,
                        new RoutingPreferenceRule(actualSegment.Id, "legacy segment"));
                    manager.AddRule(
                        RoutingPreferenceRuleGroupType.Elbows,
                        new RoutingPreferenceRule(ElementId.InvalidElementId, "welded — no elbow"));
                }
                seeded = true;
            });

            if (!seeded) return null;
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            return path;
        }
        finally
        {
            doc.Close(false);
        }
    }

    /// <summary>
    /// Runs the awaited call inline on the current (Revit API) thread —
    /// the test body already executes on it via the assembly-level
    /// RevitThreadExecutor.
    /// </summary>
    private sealed class InlineAwaitableEvent : IFamilyManagerAwaitableEvent
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _app;

        public InlineAwaitableEvent(Autodesk.Revit.ApplicationServices.Application app)
        {
            _app = app;
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            actionWithApp(_app);
            return Task.CompletedTask;
        }

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
            => Task.FromResult(funcWithApp(_app));

        public Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default)
            => asyncActionWithApp(_app);

        public void ProcessQueue(object revitApp) { }
        public void Initialize(Action onRaise) { }
    }
}
