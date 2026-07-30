---
name: smartcon-testing
description: Unit + integration testing patterns for SmartCon Revit plugin. xUnit + Moq, .NET 8 / net48 multi-version. Covers Revit API mocking limitations, test seams, fake implementations, Moq gotchas with async/generic, integration test frameworks, Jeremy Tammik recommendations. Use when writing, updating, or running tests for SmartCon.
---

# SmartCon Testing

Patterns and rules for testing the SmartCon Revit plugin. Read this BEFORE writing
tests — Revit API has unique limitations (sealed native types) that make generic
.NET testing advice misleading.

## Quick decision tree

| What you test | Approach | Tool |
|---|---|---|
| Pure logic (no Revit types in SUT) | xUnit + Moq for interfaces | `dotnet test` |
| `Document`/`ElementId`/`Family` touched by SUT | **Integration test** (ранее — нельзя было вообще) | `SmartCon.IntegrationTests` (TUnit + Nice3point.TUnit.Revit) |
| `FilteredElementCollector` inline call | Integration test OR refactor to `IFamilyFinder` (seam) | `SmartCon.IntegrationTests` |
| POCO / records / enums | xUnit equality/value tests | `dotnet test` |
| WPF ViewModel | xUnit + Moq for IExternalEventService | `dotnet test` |
| Boundary SmartCon ↔ Revit API (`SmartCon.Revit` services) | Integration test inside real Revit | `SmartCon.IntegrationTests` — см. [integration-testing.md](references/integration-testing.md) |
| WPF UI / picking / dialogs (E2E) | Ручной тест + валидация логов | `smartcon.log` |

**Rule of thumb:** if your SUT's constructor takes `IFamilyVersionStore`,
`IRevitContext`, or `IFamilyFinder` — you CANNOT unit-test the SUT directly. See
[revit-mocking.md](references/revit-mocking.md) § "What cannot be mocked" and
`docs/testing/stale-detection-coverage-gaps.md` for the real example.
**Since 2026-07 such SUTs are covered by `SmartCon.IntegrationTests`** — tests
running inside a real Revit process (see
[integration-testing.md](references/integration-testing.md)).

## Critical rules

1. **Revit API types are sealed native** — `Mock<Document>()` and
   `new ElementId(...)` throw `FileNotFoundException: RevitAPI 25.4.50.0` in CI
   because `Nice3point.Revit.Api.RevitAPI` is `ExcludeAssets=runtime`. See
   [revit-mocking.md](references/revit-mocking.md).
2. **Moq + generic nullable fails** — `Mock<IFamilyManagerAwaitableEvent>().Object`
   throws "Type contains generic type parameters" when `RaiseAsync<T>` is called
   with `T = FamilyVersion?`. **Use a hand-written fake** (see
   [test-fakes.md](references/test-fakes.md)).
3. **Moq `.Callback` without `.Returns` returns `default`** — for `Task`-returning
   methods this is `null`, breaking `await`. Always pair
   `.Setup(...).Callback(...).Returns(Task.CompletedTask)`. (Moq issue #702.)
4. **`ElementId` in tests = `new ElementId((long)N)`** — never mock. The
   `(long)` constructor is NOT deprecated; `(int)` is in Revit 2024+.
5. **xUnit 1031 (blocking Task ops)** — `.GetAwaiter().GetResult()` in sync
   tests triggers the analyzer. Either make the test `async Task` and `await`,
   or add `#pragma warning disable xUnit1031` at file top with a comment.
6. **`InternalsVisibleTo "SmartCon.Tests"`** is set in
   `SmartCon.FamilyManager.csproj` — internal SUTs (e.g. `StaleDetector`,
   `StaleCategoryAggregator`) are visible to tests. No test seam class needed
   for them.
7. **Custom `Debug.*` configurations don't define `DEBUG;TRACE`** — see
   [project-conventions.md](references/project-conventions.md) for the
   `Directory.Build.props` fix and how to verify the deployed DLL.
8. **ArgumentNullException.ThrowIfNull for net48** — wrap in
   `#if NET8_0_OR_GREATER ... #else throw new ArgumentNullException(...) #endif`
   to keep both TFM compatible.
9. **Async tests with `RaiseAsync`/`RaiseAsync<T>`** — use the
   `internal ProcessQueue(object)` test seam on
   `FamilyManagerAwaitableEvent` instead of mocking
   `IFamilyManagerAwaitableEvent` (see existing
   `FamilyManagerAwaitableEventTests.cs`).
10. **Record equality on `IReadOnlyList<T>` is reference-based** — the BCL does
    NOT compare collection contents. Compare element-by-element or expose a
    helper that does.

## Project test layout

```
src/SmartCon.Tests/
├── Core/                       # SmartCon.Core unit tests
├── FamilyManager/              # SmartCon.FamilyManager unit tests
│   ├── Core/                   # Normalizers/validators
│   ├── Events/                 # AwaitableEvent + Fakes/
│   ├── Models/                 # POCO / record tests
│   ├── Repository/             # SQLite-based tests
│   ├── Services/               # Service tests
│   ├── Stale/                  # Stale detection tests
│   └── ViewModels/             # WPF VM tests
├── TestDoubles/                # Shared fakes (FakeClock, FakeRevitContextWriter, ...)
├── TestResults/                # Coverage output (gitignored)
└── xunit.runner.json
```

**Test file naming:** `{ProductionClassName}Tests.cs`. **Test method naming:**
`{MethodUnderTest}_{StateUnderTest}_{ExpectedBehavior}` (e.g.
`MergeInto_NullExisting_StartsEmpty`).

```
src/SmartCon.IntegrationTests/      # Тесты ВНУТРИ реального Revit (TUnit + Nice3point.TUnit.Revit)
├── TestsConfiguration.cs           # RevitThreadExecutor + NotInParallel (обязательно!)
├── Support/                        # StubRevitContext, ModelSeed, SampleFiles, PipeModelFixture, ProjectViewsFixture
├── PipeConnect/                    # ConnectorWrapper/ConnectorService/ChainIterator/Mapping/Resolver/CTC
├── FamilyManager/                  # VersionStore/LoadService/DataExtraction/SnapshotExtractor (FHV3)
├── ProjectManagement/              # ModelPurge/ViewRepository/ShareSettings (ES)
└── *Tests.cs (root)                # canary + RevitTransactionService (I-03)
```

**Новый интеграционный тест пишется по образцу соседнего класса модуля.**
Перед написанием прочитай [integration-testing.md](references/integration-testing.md) —
9 жёстких правил (lazy-поля, NotInParallel, запрет RevitAPIUI, skip-гарды).

## Test seam patterns

### Constructor null-checks (Theory)

```csharp
[Theory]
[InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
[InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
public void Constructor_NullArg_Throws(int nullIndex)
{
    object[] args = { dep1, dep2, dep3, dep4, dep5, dep6, dep7, dep8 };
    args[nullIndex] = null!;
    Assert.Throws<ArgumentNullException>(() => new Sut(
        (IDep1)args[0], (IDep2)args[1], ..., (IClock)args[7]));
}
```

### Moq for safe interfaces (no Document in signatures)

```csharp
var mock = new Mock<IDependency>();
mock.Setup(m => m.Method(It.IsAny<string>())).ReturnsAsync("value");
var sut = new Sut(mock.Object, ...);
```

### Fake for interfaces with `Document`/`ElementId`

See [test-fakes.md](references/test-fakes.md) — use a hand-written class, not
`Mock<>` (the proxy fails to load `Document`).

### Async + Cancellation

```csharp
using var cts = new CancellationTokenSource();
cts.Cancel();
await Assert.ThrowsAsync<TaskCanceledException>(() => sut.MethodAsync(cts.Token));
```

### Parallel tests + integration

```csharp
[assembly: CollectionBehavior(DisableTestParallelization = true)]
// Or per collection:
[CollectionDefinition("RevitIntegration", DisableParallelization = true)]
[Collection("RevitIntegration")]
public class IntegrationTests { }
```

## Common gotchas

| Symptom | Cause | Fix |
|---|---|---|
| `FileNotFoundException: RevitAPI 25.4.50.0` in tests | `new ElementId(...)` or `Mock<IFamilyVersionStore>()` | See [revit-mocking.md](references/revit-mocking.md) — `Document` is sealed native |
| `Castle.DynamicProxy` exception at Mock construction | Generic nullable or sealed type | Hand-written fake in `TestDoubles/` |
| `Moq.Verify` reports `Times.Once` but the code DID call it | `RaiseAsync<T>` test mock returns `null` (no `.Returns(Task.FromResult(...))`) | Always pair `.Returns(Task.FromResult(...))` |
| Test passes locally, fails in CI | Test depends on `DateTimeOffset.UtcNow` | Use `TestDoubles/FakeClock` with fixed value |
| `Assert.Equal` on `StaleBatchUpdateResult` fails despite same data | Record equality compares `IReadOnlyList<T>` by reference | Compare `.TotalRequested`/`.SuccessCount`/`.FailedCount` element-by-element |
| `xUnit1031` warning on `.GetAwaiter().GetResult()` | xUnit prefers `async Task` | Make test async OR `#pragma warning disable xUnit1031` with justification comment |

## Project-specific conventions

- **Multi-version tests**: only `SmartCon.Tests.csproj` targets `net8.0-windows`
  (Revit 2025). R24/R21/R19 are .NET Framework 4.x and don't run unit tests.
  Build: `dotnet build src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25`.
- **Run tests**: `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
  --filter "FullyQualifiedName~Stale"` (or no filter for all).
- **Validate docs after test additions**: `powershell -ExecutionPolicy Bypass
  -File tools/validate-docs.ps1` — fails if new public types are undocumented.
- **Commit message style**: `test(<module>): <verb> <noun>` (e.g.
  `test(stale-detection): add 27 unit tests for snapshot POCO, SQL builder`).

## References

- [revit-mocking.md](references/revit-mocking.md) — What CANNOT be mocked, why, workarounds
- [test-fakes.md](references/test-fakes.md) — Existing fakes + how to write new ones
- [moq-patterns.md](references/moq-patterns.md) — Moq patterns: Callback, Returns, Verify, Sequences
- [integration-testing.md](references/integration-testing.md) — **SmartCon.IntegrationTests** (Nice3point.TUnit.Revit): запуск, правила, структура, паттерн «зонд»
- [autonomous-loop.md](references/autonomous-loop.md) — **Автономная разработка DB-уровня**: петля «тест ↔ лог ↔ фикс» без человека, контракт петли, стоп-условия, adversarial review
- [project-conventions.md](references/project-conventions.md) — Multi-version build, DEBUG symbol gotcha, net48/ThrowIfNull
- [jeremy-tammik.md](references/jeremy-tammik.md) — Jeremy Tammik recommendations
- [open-source.md](references/open-source.md) — GitHub examples: Speckle, ricaun, Scotec, Onbox
- [gotchas.md](references/gotchas.md) — Extended gotchas with production examples
