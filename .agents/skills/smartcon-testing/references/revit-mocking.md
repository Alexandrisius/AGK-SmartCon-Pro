# Revit API Mocking — What Cannot Be Mocked and Why

## The core problem

`RevitAPI.dll` and `RevitAPIUI.dll` are managed wrappers around a native C++
core. Almost every public class is `sealed` with `non-virtual` methods. Moq uses
Castle DynamicProxy, which **cannot** generate a proxy for sealed types or
non-virtual members. Trying throws:

```
System.ArgumentException: Invalid setup on non-overridable member:
   Autodesk.Revit.DB.Document.get_Application
```

Additionally, the test project has `Nice3point.Revit.Api.RevitAPI` with
`ExcludeAssets=runtime` (csproj line ~16) — the native `RevitAPI.dll` is not
present in CI. Even a *reference* to a `Document` parameter in a class signature
causes `GetExportedTypes()` (called by xUnit during assembly load) to throw
`FileNotFoundException: RevitAPI, Version=25.4.50.0`.

**Rule of thumb:** if your test source contains `using Autodesk.Revit.DB;` AND
the type is used in a public signature, xUnit will fail to load the test
assembly in CI.

## What CANNOT be mocked (verified empirically 2026-06-19)

| Type | Reason | Workaround |
|---|---|---|
| `Document` | sealed, native | `IRevitContext` abstraction |
| `ElementId` | sealed, but **can be created** with `new ElementId((long)N)` | Use directly, don't mock |
| `Family`, `FamilySymbol`, `FamilyInstance` | sealed, native | Wrap in interface |
| `Element` | sealed, non-virtual | `IRevitElement` wrapper holding `ElementId` |
| `UIDocument`, `UIApplication`, `UIControlledApplication` | sealed, native | `IUIDocumentContext` |
| `Transaction`, `TransactionGroup`, `SubTransaction` | sealed, native | `ITransactionService` (see ADR-I-03) |
| `FilteredElementCollector` | sealed, native | Refactor to `IFamilyFinder` |
| `Connector`, `ConnectorSet`, `ConnectorManager` | sealed, native | `IConnector` with `Origin`/`Direction`/`IsConnected` |
| `Parameter` | sealed | `IParameter` with `(name, value, type)` |
| `Wall`, `Pipe`, `Duct`, `MEPCurve` | sealed | Domain wrappers |
| `View`, `View3D`, `ViewPlan`, `ViewSheet` | sealed | `IView` |
| `Level`, `Grid`, `Room` | sealed | `ILevel`, `IGrid`, `IRoom` |
| `XYZ`, `UV`, `Transform` | sealed struct/class | **Can be created** — value-like |
| `Line`, `Arc`, `Ellipse`, `NurbSpline` | sealed | **Can be created** via static factories |
| `Solid`, `Face`, `Edge` | sealed, geometry API | Integration tests only |
| `Schema`, `Entity` (ExtensibleStorage) | sealed | `ISchemaRegistry` abstraction |
| `UpdaterRegistry`, `IUpdater` | static, native | `IUpdaterRegistry` |

## What CAN be mocked

| Type | How | Notes |
|---|---|---|
| Interfaces in `SmartCon.Core` | `Mock<IInterface>` | Most domain interfaces are safe |
| POCO / records | Direct construction | No mocking framework needed |
| Sealed methods via wrapper | `Mock<IWrapper>` then call `mock.Setup(...).Returns(...)` | Best long-term pattern |
| `XYZ`, `UV`, `Transform` | Direct construction | `new XYZ(1, 2, 3)` is fine |

## Interfaces in SmartCon that ARE safe to mock (no Document/ElementId in signatures)

Verified by reading the interfaces — these can be used with `Mock<>` directly:

- `IFamilyCatalogProvider` — methods take `string`/`CancellationToken`/POCOs only
- `IFamilyLoadService` — takes `FamilyResolvedFile` (POCO) + `FamilyLoadOptions`
- `IFamilyFileResolver` — takes `string` + `int`
- `IFamilyVersionWriter` — takes `string` + `int` + `CancellationToken`
- `IFamilyManagerDialogService` — takes `string` + POCOs (UI dialogs)
- `IFamilyManagerAwaitableEvent` — **see warning below**
- `IClock` — `DateTimeOffset UtcNow`
- `IIdGenerator`
- `IStaleDetector` (cache methods) — but `CheckFamilyAsync`/`CheckCategoryAsync`
  take `Document`/`ElementId` so SUT construction still requires safe args
- `IStaleFamilyUpdater` — does NOT take `Document`/`ElementId` directly ✓
- `IStaleCategoryAggregator` — pure logic, no Revit

## ⚠️ WARNING: `IFamilyManagerAwaitableEvent` looks safe but isn't

Signature: `Task<T> RaiseAsync<T>(Func<object, T>, CancellationToken)`.

`Mock<IFamilyManagerAwaitableEvent>().Object` **throws** when xUnit builds the
proxy because Castle DynamicProxy refuses to generate proxies for interfaces
with generic methods whose type parameter can be a nullable value type
(e.g. `FamilyVersion?`). Workaround: **hand-written fake** —
`TestDoubles/FakeFamilyManagerAwaitableEvent.cs`.

## ⚠️ WARNING: `IFamilyVersionStore` and `IRevitContext` signatures force native load

Even if you don't call any method on them, declaring
`new Mock<IFamilyVersionStore>()` makes the test class **reference**
`Document`/`ElementId` via the interface methods' parameter types. xUnit's
`GetExportedTypes()` at assembly load will try to JIT the type and throw
`FileNotFoundException: RevitAPI`. **Use a fake** (in
`TestDoubles/FakeFamilyVersionStore.cs` — currently NOT committed because of
this issue; see `docs/testing/stale-detection-coverage-gaps.md` for details).

## Recommended refactor pattern: Hexagonal / Ports & Adapters

```csharp
// SmartCon.Core/Abstractions/IRevitDocument.cs (Core layer)
public interface IRevitDocument
{
    IReadOnlyList<IFamilyWrapper> GetFamilies(Func<IFamilyWrapper, bool> predicate);
    IFamilyWrapper? GetFamily(string name);
    IDisposable StartTransaction(string name);
}

// SmartCon.Revit/Adapters/RevitDocumentAdapter.cs (Revit layer)
internal sealed class RevitDocumentAdapter : IRevitDocument
{
    private readonly Document _doc;
    public RevitDocumentAdapter(Document doc) => _doc = doc;

    public IReadOnlyList<IFamilyWrapper> GetFamilies(Func<IFamilyWrapper, bool> predicate)
    {
        return new FilteredElementCollector(_doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Select(f => (IFamilyWrapper)new RevitFamilyAdapter(f))
            .Where(predicate)
            .ToList();
    }
}

// TEST — pure Moq
var mockDoc = new Mock<IRevitDocument>();
mockDoc.Setup(d => d.GetFamilies(It.IsAny<Func<IFamilyWrapper, bool>>()))
    .Returns(new[] { Mock.Of<IFamilyWrapper>(f => f.Name == "Foo") });
```

This is the **only** way to unit-test code that uses `Document`,
`FilteredElementCollector`, `Transaction`, etc. The downside: heavy refactor of
existing code. See [open-source.md](references/open-source.md) § Scotec/Onbox
for production examples.

## ElementId in tests — the only safe way

`ElementId` has a public constructor `new ElementId(Int64)`. This DOES NOT load
the native DLL during construction (only when methods are called). However,
xUnit's `GetExportedTypes()` will fail to load the **test class** if it has
`using Autodesk.Revit.DB;` AND the type appears in a public signature — even
private fields count.

```csharp
// ✅ Safe — in code that doesn't have `using Autodesk.Revit.DB;` in test files
// (e.g. in production code or in a class not loaded by xUnit directly)
var id = new ElementId(12345L);
Assert.Equal(12345L, id.Value);

// ❌ Unsafe — test class with `using Autodesk.Revit.DB;`
//   causes xUnit assembly-load failure
public class MyTests
{
    private readonly ElementId _id = new ElementId(1L); // ← reference here breaks
}
```

**Rule:** keep `using Autodesk.Revit.DB;` **out of test source files** that
reference `Document`/`ElementId` types in any signature (field, parameter,
return type). Use fakes/wrappers instead.
