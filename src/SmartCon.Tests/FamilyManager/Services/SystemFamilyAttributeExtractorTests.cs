using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Services;
using SmartCon.Tests.FamilyManager.Events.Fakes;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Unit tests for <see cref="SystemFamilyAttributeExtractor"/>.
///
/// Scope:
/// <list type="bullet">
///   <item>no Revit — <see cref="FamilyManagerAwaitableEvent"/> runs the
///         action inline via a fake <see cref="IRevitContextWriter"/>;</item>
///   <item>extraction/save are stubbed (the real implementations require
///         a Revit <c>Document</c>);</item>
///   <item>asserts the await-extract-save-cleanup ordering guarantee
///         (the original race condition fix).</item>
/// </list>
/// </summary>
public sealed class SystemFamilyAttributeExtractorTests
{
    [Fact]
    public async Task ExtractAndSaveAsync_NoTasks_ReturnsImmediately()
    {
        var sut = new SystemFamilyAttributeExtractor(
            new InlineAwaitableEvent(),
            new StubExtraction(),
            new StubDataImport());

        await sut.ExtractAndSaveAsync(Array.Empty<SystemFamilyExtractionTask>());
    }

    [Fact]
    public async Task ExtractAndSaveAsync_NullSnapshot_SkipsAndDoesNotCallSave()
    {
        var extraction = new StubExtraction();
        var dataImport = new StubDataImport();
        var sut = new SystemFamilyAttributeExtractor(
            new InlineAwaitableEvent(),
            extraction,
            dataImport);

        // Phase 27: null snapshot means Prepare did not produce one — the
        // extractor must warn + skip (no re-open fallback in production).
        var nullSnapshotTask = new SystemFamilyExtractionTask(
            "cat-1", @"C:\non-existent\managed.rvt", new[] { "TypeA" }, null, null);

        await sut.ExtractAndSaveAsync(new[] { nullSnapshotTask });

        Assert.Equal(0, extraction.CallCount);
        Assert.Equal(0, dataImport.SaveCount);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_WithSnapshot_CallsSaveAndRetainsManagedRvt()
    {
        var managedRvt = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        File.WriteAllBytes(managedRvt, new byte[] { 0x00 });
        try
        {
            var dataImport = new StubDataImport(saveDelayMs: 50);
            var sut = new SystemFamilyAttributeExtractor(
                new InlineAwaitableEvent(),
                new StubExtraction(),
                dataImport);

            var task = new SystemFamilyExtractionTask(
                "cat-1", managedRvt, new[] { "TypeA" }, null, null,
                Snapshot: MakeSystemSnapshot("TypeA"));

            await sut.ExtractAndSaveAsync(new[] { task });

            Assert.Equal(1, dataImport.SaveCount);
            // v2.0.0: managed .rvt must remain on disk (I-16 immutable) so
            // Edit System Family can resolve it through LocalFamilyFileResolver.
            Assert.True(File.Exists(managedRvt),
                "Managed .rvt must remain in catalog storage after extraction");
        }
        finally
        {
            try { if (File.Exists(managedRvt)) File.Delete(managedRvt); } catch { }
        }
    }

    [Fact]
    public async Task ExtractAndSaveAsync_MultipleTasksWithSnapshot_AwaitsAllSaves()
    {
        var managedRvt1 = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        var managedRvt2 = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        File.WriteAllBytes(managedRvt1, new byte[] { 0x00 });
        File.WriteAllBytes(managedRvt2, new byte[] { 0x00 });
        try
        {
            var dataImport = new StubDataImport(saveDelayMs: 30);
            var sut = new SystemFamilyAttributeExtractor(
                new InlineAwaitableEvent(),
                new StubExtraction(),
                dataImport);

            var tasks = new[]
            {
                new SystemFamilyExtractionTask("cat-1", managedRvt1, new[] { "T1" }, null, null,
                    Snapshot: MakeSystemSnapshot("T1")),
                new SystemFamilyExtractionTask("cat-2", managedRvt2, new[] { "T2" }, null, null,
                    Snapshot: MakeSystemSnapshot("T2")),
            };

            await sut.ExtractAndSaveAsync(tasks);

            Assert.Equal(2, dataImport.SaveCount);
            Assert.True(File.Exists(managedRvt1));
            Assert.True(File.Exists(managedRvt2));
        }
        finally
        {
            try { if (File.Exists(managedRvt1)) File.Delete(managedRvt1); } catch { }
            try { if (File.Exists(managedRvt2)) File.Delete(managedRvt2); } catch { }
        }
    }

    [Fact]
    public async Task ExtractAndSaveAsync_SaveThrows_DoesNotPropagateAndRetainsManagedFile()
    {
        var managedRvt = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        File.WriteAllBytes(managedRvt, new byte[] { 0x00 });
        try
        {
            var dataImport = new StubDataImport(throwOnSave: true);
            var sut = new SystemFamilyAttributeExtractor(
                new InlineAwaitableEvent(),
                new StubExtraction(),
                dataImport);

            var task = new SystemFamilyExtractionTask(
                "cat-1", managedRvt, new[] { "TypeA" }, null, null,
                Snapshot: MakeSystemSnapshot("TypeA"));

            await sut.ExtractAndSaveAsync(new[] { task });

            Assert.Equal(1, dataImport.SaveCount);
            Assert.True(File.Exists(managedRvt),
                "Save failure must not delete the managed file");
        }
        finally
        {
            try { if (File.Exists(managedRvt)) File.Delete(managedRvt); } catch { }
        }
    }

    /// <summary>
    /// Builds a minimal <see cref="SystemFamilySnapshot"/> with the given type
    /// names and no parameter values — enough for <c>SnapshotExtractionMapper</c>
    /// to produce a successful <see cref="FamilyExtractionResult"/>.
    /// </summary>
    private static SystemFamilySnapshot MakeSystemSnapshot(params string[] typeNames) =>
        new(
            CategoryName: "TestCategory",
            CategoryId: -2008044,
            Types: typeNames
                .Select(n => new SystemTypeSnapshot(n, Array.Empty<SystemParameterValue>()))
                .ToList());

    /// <summary>
    /// Inlines the action on a real <see cref="FamilyManagerAwaitableEvent"/>
    /// via <c>ProcessQueue</c>. This preserves the production code path
    /// while removing the dependency on a real Revit <c>UIApplication</c>.
    /// </summary>
    private sealed class InlineAwaitableEvent : IFamilyManagerAwaitableEvent
    {
        private readonly FamilyManagerAwaitableEvent _inner;
        private readonly object _uiAppSurrogate = new();

        public InlineAwaitableEvent()
        {
            _inner = new FamilyManagerAwaitableEvent(new FakeRevitContextWriter());
            _inner.Initialize(() => _inner.ProcessQueue(_uiAppSurrogate));
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
            => _inner.RaiseAsync(actionWithApp, ct);

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
            => _inner.RaiseAsync(funcWithApp, ct);

        public Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default)
            => _inner.RaiseAsyncTask(asyncActionWithApp, ct);

        public void ProcessQueue(object revitApp)
            => _inner.ProcessQueue(revitApp);

        public void Initialize(Action onRaise)
            => _inner.Initialize(onRaise);
    }

    private sealed class StubExtraction : ISystemFamilyAttributeExtractionService
    {
        private readonly bool _forceFail;

        public StubExtraction(bool forceFail = false)
        {
            _forceFail = forceFail;
        }

        public int CallCount { get; private set; }

        public FamilyExtractionResult ExtractFromRvt(
            string rvtFilePath, IReadOnlyList<string>? typeNames)
        {
            CallCount++;
            if (_forceFail)
            {
                return new FamilyExtractionResult(
                    false, Array.Empty<FamilyExtractionTypeValues>(),
                    null, "stub forced failure", 0);
            }
            return new FamilyExtractionResult(
                true,
                new[]
                {
                    new FamilyExtractionTypeValues(
                        typeNames is { Count: > 0 } ? typeNames[0] : "StubType", 0,
                        Array.Empty<FamilyExtractionValueResult>())
                },
                null, null, 2025);
        }
    }

    private sealed class StubDataImport : IFamilyDataImportService
    {
        private readonly int _saveDelayMs;
        private readonly bool _throwOnSave;

        public StubDataImport(int saveDelayMs = 0, bool throwOnSave = false)
        {
            _saveDelayMs = saveDelayMs;
            _throwOnSave = throwOnSave;
        }

        public int SaveCount { get; private set; }

        public Task<FamilyDataImportResult> SaveExtractionResultAsync(
            string catalogItemId,
            FamilyExtractionResult extractionResult,
            string? versionId,
            string? fileId,
            CancellationToken ct = default)
        {
            SaveCount++;
            if (_throwOnSave) throw new InvalidOperationException("stub save failure");
            if (_saveDelayMs > 0) Thread.Sleep(_saveDelayMs);
            return Task.FromResult(new FamilyDataImportResult(
                true, Guid.NewGuid().ToString(),
                extractionResult.Types.Count, 0, 0, null));
        }

        public Task<FamilyDataImportResult> ImportDataAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(new FamilyDataImportResult(true, null, 0, 0, 0, null));

        public Task<FamilyExtractionPrepareResult> PrepareExtractionAsync(
            string catalogItemId, int targetRevitVersion, CancellationToken ct = default)
            => Task.FromResult(new FamilyExtractionPrepareResult(
                false, null, null, Array.Empty<string>(), null));
    }
}
