# Open Source Examples — How Other Revit Plugins Do Testing

## 1. Speckle — `xUnitRevit` (best legacy xUnit pattern)

**Repo:** <https://github.com/specklesystems/speckle-sharp/tree/main/ConnectorRevit>
**Stars:** 135
**Pattern:** IClassFixture + custom test attributes

```csharp
[Fact]
public void SendSelectionTest()
{
    var selection = new List<Element> { /* ... */ };
    var converter = new RevitConverter();
    var result = converter.Convert(selection);
    Assert.NotEmpty(result);
}

// xUnitRevitUtils.RunInTransaction(doc, () => { ... });
// xUnitRevitUtils.CreateFamilyInstance(doc, familyPath);
```

**Apply to SmartCon:** when adding RevitXunit.TestAdapter, follow the
`IClassFixture<RevitDocumentFixture>` pattern for shared document.

## 2. ricaun.RevitTest — multi-version NUnit framework

**Repo:** <https://github.com/ricaun-io/RevitTest>
**Stars:** 21
**Versions:** 2019-2025 + Preview

```csharp
[TestFixture]
public class PipeConnectTests
{
    [Test]
    public void Connect_TwoPipes_ProducesAlignedJunction()
    {
        var doc = RevitTestContext.Current.Document;
        var pipe1 = CreatePipe(doc, start: new XYZ(0,0,0), end: new XYZ(10,0,0));
        var pipe2 = CreatePipe(doc, start: new XYZ(10,0,0), end: new XYZ(10,10,0));
        var connector1 = pipe1.ConnectorManager.Connectors.Cast<Connector>().First();
        var connector2 = pipe2.ConnectorManager.Connectors.Cast<Connector>().First();
        connector1.ConnectTo(connector2);
        // Asserts
    }
}
```

**Key features:**
- Auto-install as Revit add-in (no manual setup)
- `AssemblyMetadata("Revit.Version", "2025")` for version selection
- `Design Automation for Revit` for cloud-based CI
- NUnit assertions (not xUnit)

**Apply to SmartCon:** best choice for multi-version CI. Would require
creating `SmartCon.IntegrationTests` project with NUnit.

## 3. Scotec.Revit — production-grade async pattern

**Repo:** <https://github.com/scotec-Software-Solutions-AB/scotec-revit>
**Stars:** 7
**Pattern:** DI scope per command execution

```csharp
public abstract class RevitCommand : IExternalCommand
{
    [Autowire] protected IServiceProvider ServiceProvider { get; set; }
    [Autowire] protected IRevitTask RevitTask { get; set; }

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        using var scope = ServiceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler>();
        return handler.Execute(data, ref message, elements);
    }
}

public abstract class RevitCommandHandler<TOptions> : ICommandHandler
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        return RevitTask.Run(app =>
        {
            // Revit API on UI thread
            return DoExecute(app.ActiveUIDocument.Document);
        });
    }
}
```

**Apply to SmartCon:** Scotec's `RevitTask` + `IExternalEventService`
abstraction is the same pattern SmartCon already uses (see
`IFamilyManagerAwaitableEvent` and `IRevitContextWriter`). Validate the
abstraction is sufficient or extend if needed.

## 4. Onbox Framework — DI + testability

**Repo:** <https://github.com/engthiago/Onboxframework>
**Pattern:** `Onbox.Revit.Abstractions` → `Onbox.Revit` → tests

```csharp
// Abstractions layer (no RevitAPI)
public interface IRevitDocument
{
    IReadOnlyList<IRevitElement> GetElements();
}

// Revit adapter (internal)
internal class RevitDocumentAdapter : IRevitDocument
{
    public RevitDocumentAdapter(Document doc) { _doc = doc; }
    public IReadOnlyList<IRevitElement> GetElements() =>
        new FilteredElementCollector(_doc)
            .Cast<Element>()
            .Select(e => (IRevitElement)new RevitElementAdapter(e))
            .ToList();
}

// Test
var mockDoc = new Mock<IRevitDocument>();
mockDoc.Setup(d => d.GetElements()).Returns(new[] { mockElement.Object });
```

**Apply to SmartCon:** this is the **end-state** of the refactoring effort
for Stale detection. See `docs/testing/stale-detection-coverage-gaps.md` for
how far we've gotten and what's left.

## 5. Revit.Async (KennanChan) — generic ExternalEvent

**Repo:** <https://github.com/KennanChan/Revit.Async>
**Pattern:** `IGenericExternalEventHandler<TParameter, TResult>`

```csharp
public class MyHandler : IGenericExternalEventHandler<MyParam, MyResult>
{
    public MyResult Execute(UIApplication app, MyParam param)
    {
        var doc = app.ActiveUIDocument.Document;
        // ... do work
        return new MyResult { Success = true };
    }
}

// Caller
var result = await RevitTask.RunAsync(() => new MyHandler(), new MyParam());
```

**Apply to SmartCon:** compare to `FamilyManagerAwaitableEvent`. SmartCon's
design is similar (Func<object, T> in queue) but uses AsyncLocal-based
scope flow vs KennanChan's UIApplication context. Both are valid.

## 6. RevitTestRunner (xUnit) — newest xUnit-based framework

**Repo:** <https://github.com/kristoffer-tungland/RevitTestRunner>
**Date:** June 2025 (very new, 0 stars at time of research)

```csharp
[RevitFact(@"C:\Models\sample.rvt")]
public async Task MyTest(Document doc, UIApplication app)
{
    var pipe = await RevitTask.RunAsync(() =>
        CreatePipe(doc, new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
    Assert.NotNull(pipe);
}
```

**Apply to SmartCon:** promising but immature. Wait for community adoption
before depending on it.

## 7. PyRevit — Python (different stack, but pattern worth noting)

**Repo:** <https://github.com/pyrevitlabs/pyrevit>
**Pattern:** runtime-discoverable test commands

PyRevit uses a `TEST` button pattern where each test is a CPython script that
runs inside Revit's IronPython host. This is **the only pattern** that
truly requires zero setup beyond having PyRevit installed.

**Apply to SmartCon:** not directly (we're C#), but the "discoverable
test commands in the Revit ribbon" concept could be useful for integration
testing UX.

## Pattern comparison matrix

| Project | Test framework | Unit | Integration | Multi-version | CI |
|---|---|---|---|---|---|
| Speckle | xUnitRevit | ⚠️ | ✅ | 2021-2024 | ❌ manual |
| ricaun.RevitTest | NUnit | ❌ | ✅ | 2019-2025 | ✅ |
| Scotec.Revit | (none yet) | ❌ | ❌ | net8.0 | ❌ |
| Onbox | xUnit (assumed) | ✅ | partial | multi | ✅ (DA) |
| Revit.Async | (none) | ❌ | ❌ | multi | ❌ |
| RevitTestRunner | xUnit | ❌ | ✅ | 2025+ | partial |
| PyRevit | Python REPL | ❌ | ✅ | 2017-2026 | ❌ |
| **SmartCon (current)** | xUnit + Moq | ✅ 1312 | ❌ | 2024-2025 | partial |

## Recommended reading order for new agents

1. **This file** — see which patterns match SmartCon's style
2. **Speckle ConnectorRevit tests** — most realistic xUnit pattern
3. **ricaun.RevitTest** — best multi-version + CI option
4. **Scotec.Revit** — best async pattern (matches our `IFamilyManagerAwaitableEvent`)
5. **Onbox** — best abstraction pattern (matches our `IRevitContext` etc.)
