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
    public async Task ExtractAndSaveAsync_MissingTempRvt_SkipsAndDoesNotCallSave()
    {
        var extraction = new StubExtraction();
        var dataImport = new StubDataImport();
        var sut = new SystemFamilyAttributeExtractor(
            new InlineAwaitableEvent(),
            extraction,
            dataImport);

        var missing = new SystemFamilyExtractionTask(
            "cat-1", @"C:\non-existent\temp.rvt", new[] { "TypeA" }, null, null);

        await sut.ExtractAndSaveAsync(new[] { missing });

        Assert.Equal(0, extraction.CallCount);
        Assert.Equal(0, dataImport.SaveCount);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_SuccessfulExtraction_AwaitsSaveThenDeletes()
    {
        var tempRvt = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        File.WriteAllBytes(tempRvt, new byte[] { 0x00 });
        try
        {
            var extraction = new StubExtraction();
            var dataImport = new StubDataImport(saveDelayMs: 50);
            var sut = new SystemFamilyAttributeExtractor(
                new InlineAwaitableEvent(),
                extraction,
                dataImport);

            var task = new SystemFamilyExtractionTask(
                "cat-1", tempRvt, new[] { "TypeA" }, null, null);

            await sut.ExtractAndSaveAsync(new[] { task });

            Assert.Equal(1, extraction.CallCount);
            Assert.Equal(1, dataImport.SaveCount);
            Assert.False(File.Exists(tempRvt), "Temp .rvt must be deleted after save completes");
        }
        finally
        {
            try { if (File.Exists(tempRvt)) File.Delete(tempRvt); } catch { }
        }
    }

    [Fact]
    public async Task ExtractAndSaveAsync_ExtractionFails_DoesNotCallSaveButDeletes()
    {
        var tempRvt = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        File.WriteAllBytes(tempRvt, new byte[] { 0x00 });
        try
        {
            var extraction = new StubExtraction(forceFail: true);
            var dataImport = new StubDataImport();
            var sut = new SystemFamilyAttributeExtractor(
                new InlineAwaitableEvent(),
                extraction,
                dataImport);

            var task = new SystemFamilyExtractionTask(
                "cat-1", tempRvt, new[] { "TypeA" }, null, null);

            await sut.ExtractAndSaveAsync(new[] { task });

            Assert.Equal(1, extraction.CallCount);
            Assert.Equal(0, dataImport.SaveCount);
            Assert.False(File.Exists(tempRvt), "Temp .rvt is still deleted even if extraction failed");
        }
        finally
        {
            try { if (File.Exists(tempRvt)) File.Delete(tempRvt); } catch { }
        }
    }

    [Fact]
    public async Task ExtractAndSaveAsync_MultipleTasks_AwaitsAllSaves()
    {
        var tempRvt1 = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        var tempRvt2 = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        File.WriteAllBytes(tempRvt1, new byte[] { 0x00 });
        File.WriteAllBytes(tempRvt2, new byte[] { 0x00 });
        try
        {
            var extraction = new StubExtraction();
            var dataImport = new StubDataImport(saveDelayMs: 30);
            var sut = new SystemFamilyAttributeExtractor(
                new InlineAwaitableEvent(),
                extraction,
                dataImport);

            var tasks = new[]
            {
                new SystemFamilyExtractionTask("cat-1", tempRvt1, new[] { "T1" }, null, null),
                new SystemFamilyExtractionTask("cat-2", tempRvt2, new[] { "T2" }, null, null),
            };

            await sut.ExtractAndSaveAsync(tasks);

            Assert.Equal(2, extraction.CallCount);
            Assert.Equal(2, dataImport.SaveCount);
            Assert.False(File.Exists(tempRvt1));
            Assert.False(File.Exists(tempRvt2));
        }
        finally
        {
            try { if (File.Exists(tempRvt1)) File.Delete(tempRvt1); } catch { }
            try { if (File.Exists(tempRvt2)) File.Delete(tempRvt2); } catch { }
        }
    }

    [Fact]
    public async Task ExtractAndSaveAsync_SaveThrows_DoesNotPropagateAndStillDeletes()
    {
        var tempRvt = Path.Combine(Path.GetTempPath(), $"sf-test-{Guid.NewGuid():N}.rvt");
        File.WriteAllBytes(tempRvt, new byte[] { 0x00 });
        try
        {
            var extraction = new StubExtraction();
            var dataImport = new StubDataImport(throwOnSave: true);
            var sut = new SystemFamilyAttributeExtractor(
                new InlineAwaitableEvent(),
                extraction,
                dataImport);

            var task = new SystemFamilyExtractionTask(
                "cat-1", tempRvt, new[] { "TypeA" }, null, null);

            await sut.ExtractAndSaveAsync(new[] { task });

            Assert.Equal(1, dataImport.SaveCount);
            Assert.False(File.Exists(tempRvt));
        }
        finally
        {
            try { if (File.Exists(tempRvt)) File.Delete(tempRvt); } catch { }
        }
    }

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

        public Task MergeMissingValuesAsync(
            string catalogItemId,
            FamilyExtractionResult extractionResult,
            string? versionId,
            string? fileId,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
