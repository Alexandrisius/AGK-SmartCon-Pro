# Test Gotchas — Known Issues with Code Examples

## GOTCHA 1: `FileNotFoundException: RevitAPI 25.4.50.0` in CI

**Symptom:**
```
System.IO.FileNotFoundException : Could not load file or assembly
'RevitAPI, Version=25.4.50.0, Culture=neutral, PublicKeyToken=null'.
Не удается найти указанный файл.
```

**Cause:** xUnit's `GetExportedTypes()` (called during assembly load) tries
to JIT every type. If a test class has `using Autodesk.Revit.DB;` AND a
signature uses `Document`/`ElementId`/`Family`, the JIT loads
`Autodesk.Revit.DB.Document` → tries to load native `RevitAPI.dll` → fails
because `Nice3point.Revit.Api.RevitAPI` has `ExcludeAssets=runtime`.

**Fix:** see [revit-mocking.md](revit-mocking.md) — use fakes, not Moq, for
interfaces with Revit types in signatures.

**Real example (2026-06-19):** First attempt at `StaleFamilyUpdaterTests.cs`
created `Mock<IFamilyVersionStore>()` and `Mock<IRevitContext>()`. Both
`.Object` access triggered the exception. Solution: hand-written
`FakeFamilyVersionStore` and `FakeRevitContext` — but those fakes
themselves cause the same issue because they implement the interface.
Conclusion: `StaleDetector`/`StaleFamilyUpdater` SUT tests are impossible
in unit tests. See `docs/testing/stale-detection-coverage-gaps.md`.

## GOTCHA 2: `Mock<IFamilyManagerAwaitableEvent>().Object` fails on generic nullable

**Symptom:**
```
System.ArgumentException: Type contains generic type parameters.
```

**Cause:** `IFamilyManagerAwaitableEvent.RaiseAsync<T>(Func<object, T>, CT)`
called with `T = FamilyVersion?` (nullable value type). Castle DynamicProxy
refuses to generate proxies for such combinations.

**Fix:** use `TestDoubles/FakeFamilyManagerAwaitableEvent` — hand-written
implementation that bypasses Castle.

**Real example:** `Mock<IFamilyManagerAwaitableEvent>().Object` throws
even for setup-only calls in `StaleDetectorTests.cs`. Fake works.

## GOTCHA 3: Moq `.Callback` without `.Returns` returns null Task

**Symptom:**
```
System.NullReferenceException : Object reference not set to an instance of an object.
at System.Threading.Tasks.Task.ThrowIfNull(...)
```

**Cause:** `Task<T>` returned by Moq for an async method setup with
`.Callback` only (no `.Returns`) is `null`. Awaiting null throws.

**Fix:** always pair `.Callback(...).Returns(Task.FromResult(...))` or use
`.ReturnsAsync(...)`:

```csharp
// ❌ BROKEN
mock.Setup(m => m.GetAsync(It.IsAny<string>()))
    .Callback<string>(arg => captured = arg);

// ✅ CORRECT
mock.Setup(m => m.GetAsync(It.IsAny<string>()))
    .Callback<string>(arg => captured = arg)
    .Returns(Task.FromResult("value"));

// ✅ ALSO CORRECT
mock.Setup(m => m.GetAsync(It.IsAny<string>()))
    .Callback<string>(arg => captured = arg)
    .ReturnsAsync("value");
```

See [Moq issue #702](https://github.com/devlooped/moq/issues/702).

## GOTCHA 4: `xUnit1031` blocks `.GetAwaiter().GetResult()` in sync tests

**Symptom:**
```
warning xUnit1031: Test methods should not use blocking task operations,
as they can cause deadlocks. Use an async test method and await instead.
```

**Fix options:**

```csharp
// Option A: make test async
[Fact]
public async Task MyTest() { await sut.MethodAsync(); }

// Option B: pragma with justification
#pragma warning disable xUnit1031  // intentional blocking in sync setup
[Fact]
public void MyTest() { sut.MethodAsync().GetAwaiter().GetResult(); }
```

**Note:** for Revit-specific code, `.GetAwaiter().GetResult()` is actually
safe (no real `await` in the underlying sync wrapper) — see
`revit-api-best-practice` skill § "Never Use .Result/.GetResult() Without
Task.Run()". The deadlock is when the method internally `await`s something.

## GOTCHA 5: `StaleUpdateRequest` casing mismatch

**Symptom:**
```
error CS1739: Наиболее подходящий перегруженный метод для "StaleUpdateRequest"
не имеет параметр с именем "overwriteParameterValues".
```

**Cause:** Naming convention mismatch between interface and model:
- `IStaleFamilyUpdater.UpdateFamilyAsync(string, bool overwriteParameterValues, CT)` — lowercase
- `StaleUpdateRequest(IReadOnlyList<string>, bool OverwriteParameterValues, ...)` — uppercase

**Fix:** use the correct case for each. Not a bug, but easy to typo.

## GOTCHA 6: `ElementId((int)1)` deprecated in Revit 2024

**Symptom:**
```
warning CS0618: ElementId.ElementId(int) is deprecated
```

**Fix:** use `new ElementId((long)1)` — the `(long)` overload is NOT
deprecated. Verify in test code that doesn't break under both Revit 2024
and 2025.

## GOTCHA 7: `xUnit.InRange` vs `Assert.True` with `&&` conditions

**Symptom:** test fails on edge values

```csharp
// ❌ Brittle — exact match may fail on slow CI
Assert.Equal(42, result);

// ✅ Use InRange for timing-related assertions
Assert.InRange(actual, expected - 0.001, expected + 0.001);
```

For `SmartConLogger.Debug` performance tests, always use `InRange`.

## GOTCHA 8: `record` equality on `IReadOnlyList<T>` is reference-based

**Symptom:**
```
Assert.Equal() Failure: Values differ
Expected: StaleBatchUpdateResult { ..., SuccessCatalogItemIds = <>z__ReadOnlyArray`1[...] }
Actual:   StaleBatchUpdateResult { ..., SuccessCatalogItemIds = <>z__ReadOnlySingleElementList`1[...] }
```

**Cause:** BCL record equality compares `IReadOnlyList<T>` by reference, not
content. Two `["x"]` instances with same content are NotEqual.

**Fix:** compare element-by-element:

```csharp
// ❌ BROKEN for record equality
Assert.Equal(expected, actual);

// ✅ CORRECT
Assert.Equal(expected.TotalRequested, actual.TotalRequested);
Assert.Equal(expected.SuccessCount, actual.SuccessCount);
Assert.Equal(expected.FailedCount, actual.FailedCount);
Assert.Equal(expected.SkippedCount, actual.SkippedCount);
Assert.Equal(expected.SuccessCatalogItemIds, actual.SuccessCatalogItemIds);  // both are same instance
```

## GOTCHA 9: `using var _ =` + lambda `_ =>` collision (CS0136)

**Symptom:**
```
error CS0136: A local or parameter named '_' cannot be declared in this scope
because that name is used in an enclosing local scope to declare a local or parameter.
```

**Cause:** `using var _ = ...` declares `_`, then `_ => { ... }` lambda
captures the same `_`.

**Fix:** use a different name:
```csharp
using var _scope = SmartConLogger.BeginScope(...);  // not `using var _`
```

**Project rule:** `_scope` is the standard name in SmartCon for `BeginScope`
return values.

## GOTCHA 10: Tests depend on `DateTimeOffset.UtcNow`

**Symptom:** test passes locally, fails in CI with timestamp mismatches

**Fix:** use `TestDoubles/FakeClock` with a fixed value:

```csharp
var clock = new FakeClock(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero));
var sut = new SUT(..., clock);
// Assert exact timestamp
Assert.Equal(clock.UtcNow, snapshot.CheckedAtUtc);
```

**Production example:** `StaleSnapshotLogic.MergeInto(snapshot, results, now)`
takes `now` as a parameter — SUTs that pass `_clock.UtcNow` should use
`FakeClock` in tests for determinism.

## GOTCHA 11: `IClassFixture` race condition on async teardown

**Symptom:** "DisposeAsync is still executing while the next test is starting
to run" — flaky tests

**Source:** [xUnit IAsyncLifetime issue](https://stackoverflow.com/questions/78875060/xunit-fixture-with-iasynclifetime-not-working-as-intendend)

**Fix:** use `[Collection("Name", DisableParallelization = true)]` for
fixtures with async cleanup.

## GOTCHA 12: `InternalsVisibleTo` mismatch after rename

**Symptom:** test fails to compile with "type 'StaleDetector' is inaccessible
due to its protection level"

**Cause:** the production csproj has
`<InternalsVisibleTo Include="SmartCon.Tests" />` but the test project's
`AssemblyName` doesn't match.

**Fix:** verify `SmartCon.Tests.csproj` has
`<AssemblyName>SmartCon.Tests</AssemblyName>` (default), and
`SmartCon.FamilyManager.csproj` has the matching `InternalsVisibleTo`.

## GOTCHA 13: `Coverlet` only runs with `XPlat Code Coverage` collector

**Symptom:** no `coverage.cobertura.xml` in `TestResults/`

**Fix:** ensure `--collect:"XPlat Code Coverage"` flag is passed to
`dotnet test`. Default doesn't generate coverage.

## GOTCHA 14: `IExternalEventService` mock returns `Task.CompletedTask` but SUT awaits non-`Task`

**Symptom:** deadlock in integration test, works in unit test

**Cause:** if the SUT awaits a method that internally calls Revit API
through a `Task.Run` wrapper, the unit test (no Revit) completes
immediately, but in integration the `Task.Run` shifts the call to
ThreadPool → Revit API on non-main thread → hang or crash.

**Fix:** see `revit-api-best-practice` skill § "IDropHandler.Execute +
LoadFamily Async Pattern" for the SAFE/DANGEROUS distinction. The rule:
`.GetAwaiter().GetResult()` directly is SAFE for sync wrappers
(`Task.FromResult`); `AsyncBridge.RunSync` (= `Task.Run` + `GetResult`) is
DANGEROUS for methods that internally call Revit API.
