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

> **Note:** "Nested types" are no longer a cause of warnings — every public type in Core has its own .cs file (1 type = 1 file convention).

## Multi-module types

If a type logically belongs to multiple modules, document it in **one** file (the primary module) and link to it from the others:

```markdown
In `docs/domain/models/family-manager.md`:
## FamilyMetadataPackage
(...)
См. также: [FamilyMetadataExportPackage в `family-manager-loadable.md`](family-manager-loadable.md#familymetadataexportpackage)
```

## Validator exit codes

| Exit | Meaning | Action |
|---|---|---|
| 0 | PASSED | Proceed with commit |
| 1 | FAILED | Add missing docs OR remove broken headings, re-run |
| 2 | STRUCTURAL | docs/domain/ structure broken — check README files exist |
