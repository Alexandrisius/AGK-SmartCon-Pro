# Quick reference: adding docs for a new type

## TL;DR for a model in `SmartCon.Core/Models/PipeConnect/`

1. Add `## TypeName` section in `docs/domain/models/pipeconnect.md` (or create new module file)
2. Format:
   ```markdown
   ## TypeName

   Краткое описание.

   **Файл:** `SmartCon.Core/Models/PipeConnect/TypeName.cs`

   ```csharp
   public sealed class TypeName
   {
       public string Foo { get; init; }
   }
   ```
   ```
3. Run `powershell -File tools\validate-docs.ps1` → expect `PASSED`

## TL;DR for an interface in `SmartCon.Core/Services/Interfaces/`

Same as above but the file goes to `docs/domain/interfaces/<module>.md` and the section starts with `## ITypeName`.

## Where to put a FamilyManager type

FamilyManager docs live in **subfolders**, not a single file:

- Models → `docs/domain/models/family-manager/<topic>.md`
- Interfaces → `docs/domain/interfaces/family-manager/<topic>.md`

Pick the topic that fits (`catalog`, `import`, `stale-detection`, `extraction`, ...).
The full topic list is in the subfolder's `README.md`. If none fits, create a new
topic file with the same `module: family-manager` frontmatter and add it to that index.

## File too big? Split it

Hard limit: **1000 lines per .md file** (validator prints `[WARN]` above that).
At ~900 lines start splitting:

1. `models/<module>.md` → `models/<module>/<topic>.md` + `models/<module>/README.md` (index)
2. Keep `module: <module>` frontmatter in every topic file
3. `## TypeName` only for types — never for topic-group headers (validator parses H2-H4)
4. Delete the old flat file, update `models/README.md` (or `interfaces/README.md`)
5. Re-run validator → `PASSED`, no oversized warnings

## If the file has BOTH a non-matching type first and the matching type

Example: `IFamilyManagerDialogService.cs` has `public enum DialogResult` then `public interface IFamilyManagerDialogService`.

The validator scans for the **type whose name matches the file basename** (`IFamilyManagerDialogService`). It finds the `interface` declaration (line 17), not the `enum` (line 5). So classification is correct → file goes to `interfaces/`.

But if the file has `public class Container` and `public interface IFoo` (where filename is `IFoo.cs`), the validator looks for a declaration named `IFoo`, finds the `interface`, classifies as interface. The first-declaration `class Container` is ignored.

## Orphan warning cheat sheet

| Warning | Cause | Fix |
|---|---|---|
| `IFoo exists in Core but is NOT documented` | Missing `## IFoo` heading anywhere | Add the heading |
| `[module] Foo (file.md:N)` orphan | Documented but no `.cs` file matches | If type was removed, delete section. If in non-Core assembly, leave as-is. |
| `[module] IRevitUIContext (file.md:N)` | Type lives in `SmartCon.Revit/`, not Core | Leave as-is (validator scope is Core) |
| `[module] IFamilyManagerViewModelFactory` | Type lives in `SmartCon.FamilyManager/`, not Core | Leave as-is (Clean Architecture boundary) |
| `[module] FamilyManagerServices Aggregate` | Sub-heading, not a type | Convert to a paragraph or callout |
| `<file>.md (NNNN lines > 1000)` oversized | File grew past the split threshold | Split into `<module>/<topic>.md` (see above) |

> **Note:** "Nested types" are no longer a cause of warnings — every public type in Core has its own .cs file (1 type = 1 file convention).

## Multi-module types

If a type logically belongs to multiple modules, document it in **one** file (the primary module) and link to it from the others:

```markdown
In `docs/domain/models/family-manager/import.md`:
## PreparedFamilyItem
(...)
См. также: [SelectedElementsAnalysis в `catalog.md`](catalog.md#selectedelementsanalysis)
```

## Validator exit codes

| Exit | Meaning | Action |
|---|---|---|
| 0 | PASSED | Proceed with commit |
| 1 | FAILED | Add missing docs OR remove broken headings, re-run |
| 2 | STRUCTURAL | docs/domain/ structure broken — check README files exist |
