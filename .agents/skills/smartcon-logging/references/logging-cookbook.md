# Logging Cookbook — Recipes for Common Scenarios

Step-by-step recipes for the five most common logging tasks. Each one
shows the working code, the rationale, and the gotchas to avoid.

For the underlying API, see `SKILL.md`. For lessons learned from real
audits, see `known-issues.md`. For L8 / L9 / C1 / C5 / C15 conventions
added during the most recent cleanup, see `recent-patterns.md`.

---

## Recipe 1: Add logging to a new public service method

You're adding `IFamilyXService.DoSomethingAsync(filePath, ct)` and need
to log when it's called, what it does, and the result.

### Code

```csharp
public async Task<X> DoSomethingAsync(string filePath, CancellationToken ct = default)
{
    using var _scope = SmartConLogger.BeginScope("MyCategory",
        ("Method", nameof(DoSomethingAsync)),
        ("FilePath", Path.GetFileName(filePath)),
        ("ElementId", elementId.IntegerValue));

    SmartConLogger.Info("Starting");
    var result = await SomeInnerCall(ct);
    SmartConLogger.Info($"Got {result.Count} items");
    return result;
}
```

### What's in the scope

- **`"MyCategory"`** — pick from the vocabulary table in `SKILL.md` ("Adding a new BeginScope"). If your method is in a class named `FamilyXyzService`, the category is usually `FamilyXyz` (no "Service" suffix).
- **`("Method", nameof(DoSomethingAsync))`** — use `nameof`, never a string literal. Refactor-safe.
- **`("FilePath", Path.GetFileName(filePath))`** — basename, not full path. See L8 in `recent-patterns.md`.
- **`("ElementId", elementId.IntegerValue)`** — for R21–R23, `.IntegerValue` returns `int`. For R24+ use `.Value` (returns `long`). The codebase currently targets both, so use the version-appropriate property.

### What's in the messages

- **Short, no path/file name duplication.** The scope already carries `FilePath=…`. Repeating it in the message is the #1 source of log spam.
- **No manual `[Cat] ` prefix.** The scope chain is the prefix.
- **No `$"[{attemptName}] …"`** — add `("Attempt", attemptName)` to the scope instead (D4 fix).

### After the commit

Every line emitted from inside `DoSomethingAsync` (including from `SomeInnerCall` if it opens its own scope) carries a `[OpId=abc12345 Op=MyCategory Method=DoSomethingAsync FilePath=ADSK_…rfa]` prefix. `grep "OpId=abc12345"` filters to this call's full subtree.

---

## Recipe 2: Add SESSION markers to a top-level user action

User invokes a Revit command, drags a family into the canvas, or clicks
a button in a WPF dialog. You want a visual `SESSION START / SESSION END`
banner in the log so an operator can find the lifecycle.

### Code

```csharp
public Result Execute(ExternalCommandData data, ref string msg, ElementSet elems)
{
    var startedAt = DateTime.Now;
    SmartConLogger.LogSessionStart(nameof(MyCommand));

    using var _scope = SmartConLogger.BeginScope("MyCategory",
        ("Method", "Execute"));

    try
    {
        // … do work, possibly calling service methods that have their own scopes …
        SmartConLogger.LogSessionEnd(nameof(MyCommand), startedAt);
        return Result.Succeeded;
    }
    catch (Exception ex)
    {
        SmartConLogger.Error($"Failed: {ex.GetType().Name}: {ex.Message}");
        SmartConLogger.LogSessionEnd(nameof(MyCommand), startedAt);  // also log on failure
        return Result.Failed;
    }
}
```

### When to use

- `IExternalCommand.Execute` (Revit ribbon button)
- WPF dialog `Button.Click` handler for the main OK button
- Drop handler entry point (`FamilyPlacementDropHandler.OnDrop`)

### When NOT to use

- Service methods called by the above. A service method has its own `BeginScope`; the caller's `LogSessionStart` already provides the visual marker for the user's action.

### Gotchas

- **Pair Start and End.** Always. Even on exception — use `try/finally` or two `LogSessionEnd` calls.
- **Pass `startedAt` as `DateTime.Now` BEFORE the work begins.** The `End` banner shows elapsed seconds since then.
- **Don't put `LogSessionStart` inside a service method.** It would create a banner for every internal call, drowning the log.

---

## Recipe 3: Add logging to a hot loop

A method iterates over hundreds of items (connectors, families, chain
links, lookup table rows) and you want to log progress without drowning
the log.

### Code

```csharp
public void ProcessBatch(IReadOnlyList<Item> items)
{
    var counter = new HotLoopCounter(sampleEvery: 1024);
    foreach (var item in items)
    {
        DoWork(item);

        if (counter.ShouldLog())
            SmartConLogger.Debug($"processed {counter.Count}/{items.Count}");
    }
    SmartConLogger.Info($"done: {items.Count} items");
}
```

### When to use

- Any loop with ≥ 100 iterations where you want per-iteration visibility.
- The default `sampleEvery: 1024` is a safe starting point.

### When NOT to use

- Single-shot methods. The `if` check overhead isn't worth it.
- Loops where you need to log every iteration for correctness (rare).

See `counter-pattern.md` for choosing `sampleEvery` and the
`MinLevel` guard alternative.

---

## Recipe 4: Add logging to an `IExternalEventHandler` callback

The callback runs on Revit's UI thread. Scope flows via `AsyncLocal`
through `IFamilyManagerAwaitableEvent`, but you must be careful about
where you put the `BeginScope` and how you log from inside.

### Code

```csharp
public class MyAwaitableHandler : IFamilyManagerAwaitableEvent
{
    public async Task<X> RaiseAsync(Func<UIApplication, X> work, CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        SmartConLogger.LogSessionStart("MyOperation");

        using var _scope = SmartConLogger.BeginScope("MyAwaitable",
            ("Method", "RaiseAsync"));

        try
        {
            // work runs on Revit UI thread; AsyncLocal scope flows into the lambda
            var result = await _externalEvent.RaiseAsync(uiapp => work(uiapp), ct);
            SmartConLogger.LogSessionEnd("MyOperation", startedAt);
            return result;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.GetType().Name}: {ex.Message}");
            SmartConLogger.LogSessionEnd("MyOperation", startedAt);
            throw;
        }
    }
}
```

### Gotchas

- **Scope flows via `AsyncLocal`.** The `BeginScope` and the callback's log calls share the same `AsyncLocal<ScopeNode?>`, so all log lines inside the callback have the prefix.
- **Don't open a second `BeginScope` inside the callback lambda.** The outer one is already there.
- **If the inner work is `async`, log AFTER every `await` not before.** The scope is alive across awaits, but the sync context may change — see `scope-api.md` §"Threading model".
- **Sync-over-async is forbidden on UI thread.** Use `AsyncBridge.RunSync` (see C5 in `recent-patterns.md`).

---

## Recipe 5: Refactor a legacy method from `[Cat] msg` to scope

The method has:

```csharp
public void OldMethod(string path)
{
    SmartConLogger.Info($"[FileInfoReader] Detected Revit version {v} from file: {Path.GetFileName(path)}");
    SmartConLogger.Info($"[FileInfoReader] File size: {size} bytes");
    SmartConLogger.Info($"[FileInfoReader] File exists: {exists}");
}
```

### After

```csharp
public void OldMethod(string path)
{
    using var _scope = SmartConLogger.BeginScope("FileInfoReader",
        ("Method", nameof(OldMethod)),
        ("FilePath", Path.GetFileName(path)));

    SmartConLogger.Info($"Detected Revit version {v}");
    SmartConLogger.Info($"File size: {size} bytes");
    SmartConLogger.Info($"File exists: {exists}");
}
```

### What changed

1. **Removed `[FileInfoReader] ` prefix** from all three messages.
2. **Removed `from file: ADSK_…rfa`** from the first message — the scope chain now carries `FilePath=ADSK_…rfa`.
3. **Added `Method` property** to the scope (so the operation identity is queryable in log aggregators).
4. **Removed `Path.GetFileName(path)` from inside the message** — it's now in the scope (computed once on scope open, not per message).

### Audit checklist after the refactor

- [ ] `rg 'SmartConLogger\.\w+\(\s*\$"\[' --type cs src/ | grep OldMethod` → 0
- [ ] `rg "Path.GetFileName" OldMethod.cs` → 0 (no path in messages)
- [ ] `dotnet build -c Debug.R25` → 0 warnings
- [ ] Run a smoke test, capture `smartcon.log` tail, grep `OpId=` for the new scope to confirm all 3 lines have the prefix.

---

## See also

- `SKILL.md` — public API, category vocabulary, lint rules
- `scope-api.md` — `BeginScope` / `Measure` / `AsyncLocal` semantics, edge cases
- `known-issues.md` — D1…D5 lessons from real audits
- `counter-pattern.md` — `HotLoopCounter` API and `MinLevel` guard alternative
- `recent-patterns.md` — L8 / L9 / C1 / C5 / C15 conventions added in commits `bbd4b93`…`e437f35`
