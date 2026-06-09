# Known Logging Issues — Lessons from Phase 1 Audit

Defects found during Phase 1 final log audit. Documented so future migrations don't reintroduce them.

## D1: `Op=X Op=X` duplicate in scope prefix

**Symptom in log:**
```
[OpId=02b47de7 Op=FMImport Method=ImportActiveFileAsync] [OpId=af35b8b7 Op=ImportActiveFileAsync] ======================================================================
```

**Root cause:** `LogScope.FormatPrefix()` always renders `Op={Operation}` (because `LogScope.Operation` is the category name, not a structured property). Callers that ALSO add `("Op", operation)` to properties got it rendered twice.

There were two sources of the duplicate `Op=` property:
1. `SmartConLogger.Measure(operation)` — added `("Op", operation)` to the scope's `Properties` (legacy implementation).
2. `BeginScope("FMImport", ("Method", "ImportActiveFileAsync"), ("Op", "ImportActiveFileAsync"))` — explicit `("Op", ...)` in caller code (defensive but redundant).

**Fix (commit 56360f9):**
1. `Measure` no longer adds `("Op", operation)` to `Properties`. The operation name is rendered only via `FormatPrefix`'s `Op=…` segment.
2. `FormatPrefix` skips any property whose `Key == "Op"` AND whose `Value.Equals(Operation)`.

**How to recognize in audit:**
```bash
rg 'BeginScope\("([^"]+)",\s*\("Op",' --type cs src/
```
Count should be 0.

## D2: `LogScopeExtensions` helper never used

**Symptom:** `src/SmartCon.Core/Logging/LogScopeExtensions.cs` existed as a passthrough wrapper:
```csharp
public static IDisposable BeginScope(string category) =>
    SmartConLogger.BeginScope(category);
```
Zero call sites. Pure indirection. Confused readers about which API to use.

**Fix (commit e011d6c):** File deleted. All callers use `SmartConLogger.BeginScope` directly.

**Lesson:** If you add a helper extension, ensure at least one caller actually uses it within the same PR. If not, the helper is dead code.

## D3: Nested `BeginScope` + `Measure` in the same method

**Symptom in log:** Two `OpId=` prefixes on the same line, both pointing to the same conceptual operation:
```
[OpId=02b47de7 Op=FMImport Method=ImportActiveFileAsync] [OpId=af35b8b7 Op=ImportActiveFileAsync] SESSION START: ImportActiveFile
```

**Root cause:** The method body was:
```csharp
using var _scope = SmartConLogger.BeginScope("FMImport", ("Method", "ImportActiveFileAsync"));
using var _ = SmartConLogger.Measure(nameof(ImportActiveFileAsync));
```
Two `IDisposable`s in the same `using` block, each opens its own scope, each generates its own `OpId`. The Measure's `Operation="ImportActiveFileAsync"` is rendered as `Op=ImportActiveFileAsync` in its scope, which sits on top of `Op=FMImport` from the outer scope.

**Fix (commit 37ec62e):** Removed `Measure` (the `BeginScope` already gives correlation; timing is a side benefit). Pick one.

**How to recognize in audit:**
```bash
# Files that have BOTH BeginScope and Measure in the same method
rg -l 'SmartConLogger\.BeginScope' src/ | xargs -I{} sh -c 'rg -l "SmartConLogger\.Measure" "{}"'
```
Check each file — if Measure is the only scope, OK. If BeginScope is the parent, remove Measure.

## D4: `[{attemptName}]` manual prefix

**Symptom:**
```csharp
SmartConLogger.Info($"[{attemptName}] Family '{displayName}' loaded successfully");
// In log: [Attempt1] Family 'X' loaded successfully
```

**Root cause:** Caller wanted to indicate which attempt (1 or 2) produced the message. Used a manual prefix because the surrounding `BeginScope` only had `("Method", "TryLoadInTransaction")` and the `attemptName` was lost.

**Fix (commit 5d73dad):**
```csharp
using var _scope = SmartConLogger.BeginScope("FamilyLoad",
    ("Method", "TryLoadInTransaction"),
    ("Attempt", attemptName));
// Then: SmartConLogger.Info(msg);  // no [Attempt1] prefix needed
// In log: [OpId=... Op=FamilyLoad Method=TryLoadInTransaction Attempt=Attempt1] Family 'X' loaded successfully
```

**Lesson:** If a parameter would have been a manual prefix, add it as a scope property.

## D5: `[ActiveClassifier]` prefix missed in `ActiveDocumentClassifier`

**Symptom:** 3 log lines outside scope:
```
[ActiveClassifier] Active doc: title='…', isFamily=True → Family
```

**Root cause:** During Batch 9 of the Phase 1 migration, scope was added (`BeginScope("ActiveClassifier", ("Method", "ClassifyAsync"))`) but the manual `[ActiveClassifier]` prefix was left in the message string. The bulk-replace pass only handled `[Cat] ` followed by a space — and these strings used ` $"[ActiveClassifier] "` (also followed by a space). They should have been caught.

**Fix (commit 37ec62e):** Removed 3 prefix occurrences from the message strings.

**Lesson:** When adding scope, the SAME commit must remove the equivalent manual prefix. Don't separate these into different commits.

## General audit methodology

```powershell
# 1. Find all lines without OpId (potential orphans)
Get-Content "$env:APPDATA\AGK\SmartCon\smartcon.log" -Tail 1000 |
  Where-Object { $_ -notmatch '\[OpId=' } |
  Select-String "\[A-Z"  # Filter to lines that look like a category prefix

# 2. Find source of each orphan prefix
rg "<CategoryName>" --type cs src/

# 3. For each found, check whether the surrounding code already has BeginScope
$file = "src\..."
$hasScope = rg -q "SmartConLogger\.BeginScope" $file
$hasPrefix = rg -q '\$\"\[' $file
if ($hasScope -and $hasPrefix) { Write-Host "D5 in $file" }

# 4. Same for double-scope detection
$hasBoth = rg -l "BeginScope.*BeginScope|Measure.*BeginScope" --type cs src/
```

## Future migration checklist

When migrating a new module to scope-based logging:

- [ ] All `[Category]` prefixes removed from message strings
- [ ] `BeginScope` (not `Measure`) added to the public method that the user invokes
- [ ] No `Measure` call inside a method that already has `BeginScope`
- [ ] `Op==Method` and `Op==Op` dups: pick distinct operation and method names
- [ ] For parameters that would have been prefixes (like `attemptName`, `label`): add as scope property
- [ ] `LogSessionStart` only on top-level user actions, paired with `BeginScope`
- [ ] Test in Revit, capture `smartcon.log` tail, audit for orphan lines
- [ ] Build: 4/4 configs, 0 warnings
- [ ] Tests: 0 failures
