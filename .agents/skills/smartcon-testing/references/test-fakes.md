# Test Fakes — Existing Implementations and How to Write New Ones

## Why fakes (not Moq)?

For interfaces whose methods reference sealed Revit types (`Document`,
`ElementId`, `Family`, `Connector`), Moq fails because:

1. Castle DynamicProxy cannot proxy sealed types
2. xUnit's `GetExportedTypes()` fails to load the test assembly because the
   type reference forces `Document` JIT compilation → `FileNotFoundException`

**Solution:** hand-written fakes in `src/SmartCon.Tests/TestDoubles/`.

## Existing fakes

### `TestDoubles/FakeClock.cs`

```csharp
public sealed class FakeClock : IClock
{
    public FakeClock() { UtcNow = new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero); }
    public FakeClock(DateTimeOffset value) { UtcNow = value; }
    public DateTimeOffset UtcNow { get; set; }
}
```

- Default constructor: Unix epoch (2026-06-19) for deterministic tests
- Value constructor: explicit value
- Setter for time-mutation tests (rare)

**Use when:** SUT reads `IClock.UtcNow` (e.g. for snapshot timestamps).
Default value is sufficient in 95% of cases.

### `TestDoubles/FakeFamilyManagerAwaitableEvent.cs`

Hand-written implementation of `IFamilyManagerAwaitableEvent` that calls
callbacks synchronously. **Bypasses** Castle DynamicProxy's refusal to proxy
`RaiseAsync<T>` with `T = nullable value type`.

```csharp
public sealed class FakeFamilyManagerAwaitableEvent : IFamilyManagerAwaitableEvent
{
    public int RaiseCallCount { get; private set; }
    public int RaiseAsyncCallCount { get; private set; }
    public int RaiseAsyncTaskCallCount { get; private set; }

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        RaiseCallCount++;
        ct.ThrowIfCancellationRequested();
        actionWithApp(null!);
        return Task.CompletedTask;
    }

    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        RaiseAsyncCallCount++;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(funcWithApp(null!));
    }

    public Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default)
    {
        RaiseAsyncTaskCallCount++;
        ct.ThrowIfCancellationRequested();
        return asyncActionWithApp(null!);
    }

    public void ProcessQueue(object revitApp) { }
    public void Initialize(Action onRaise) { }
}
```

**Use when:** SUT uses `IFamilyManagerAwaitableEvent` to marshal to Revit
main thread. The fake's synchronous execution is sufficient for unit tests
because the callback runs on the test thread.

**For true FIFO + cancellation tests**, use the production class directly via
`internal ProcessQueue(object)` test seam — see
`FamilyManager/Events/FamilyManagerAwaitableEventTests.cs` for the canonical
pattern.

### `FamilyManager/Events/Fakes/FakeRevitContextWriter.cs` (existing)

```csharp
internal sealed class FakeRevitContextWriter : IRevitContextWriter
{
    public int CallCount { get; private set; }
    public object? LastContext { get; private set; }
    public void SetContext(object revitUIApplication) { ... }
}
```

**Use when:** testing `FamilyManagerAwaitableEvent` — captures
`IRevitContextWriter.SetContext(revitUIApplication)` calls.

## How to write a new fake

### Decision: when do you need a fake?

You need a fake (not Moq) when:
- The interface's methods have `Document`/`ElementId`/`Family` parameters or
  return types (forces native Revit API load)
- The interface has generic methods with nullable value type parameters
  (Castle DynamicProxy refuses)
- You need full control over the implementation (e.g. counting calls,
  capturing arguments, simulating errors)

### Template

```csharp
using <ProductionNamespace>;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Hand-written <see cref="IInterface"/> fake for unit tests.
/// Bypasses Moq because: [reason — Document parameter, generic nullable, etc.]
/// </summary>
internal sealed class FakeInterfaceName : IInterface
{
    // 1. Public properties for assertion (call counts, captured args)
    public int MethodCallCount { get; private set; }
    public string? LastArg { get; private set; }

    // 2. Optional: settable behavior (e.g. throw on next call)
    public Exception? ThrowOnNextCall { get; set; }

    // 3. Implementation of interface methods
    public Task<ReturnType> MethodAsync(string arg, CancellationToken ct = default)
    {
        MethodCallCount++;
        LastArg = arg;

        if (ThrowOnNextCall is not null)
        {
            var ex = ThrowOnNextCall;
            ThrowOnNextCall = null;
            throw ex;
        }

        return Task.FromResult(default(ReturnType));
    }

    // 4. For async methods, ALWAYS return Task.FromResult(...) — never null
}
```

### Naming conventions

- File: `Fake{InterfaceName}.cs` in `src/SmartCon.Tests/TestDoubles/`
- Class: `Fake{InterfaceName}` (drop the `I` prefix)
- Namespace: `SmartCon.Tests.TestDoubles`
- Visibility: `internal sealed` (matches the existing fakes)

### Don't duplicate existing fakes

Before creating a new fake, search the project:

```bash
grep -r "interface I" src/SmartCon.Tests/TestDoubles/ src/SmartCon.Tests/
```

If an existing fake implements the interface, **use it** or **extend it** —
don't create a parallel fake.
