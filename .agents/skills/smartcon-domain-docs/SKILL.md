---
name: smartcon-domain-docs
description: Conventions for SmartCon domain documentation in docs/domain/. Use when adding a new domain class or interface to SmartCon.Core, when updating existing model/interface docs, when running or interpreting the validate-docs.ps1 validator, when reorganizing docs/domain/ structure, when a docs file approaches the 1000-line split threshold, or when the user references the domain documentation validator. Covers the docs/domain/{models,interfaces}/ layout, the per-module .md file split, module subfolders for large modules (models/<module>/<topic>.md), the frontmatter convention, the validator's classification logic, and known orphan sources (nested types, cross-assembly types, sub-headings).
---

# SmartCon Domain Documentation Conventions

SmartCon keeps a **1:1 mirror** between `src/SmartCon.Core/` source code and `docs/domain/` Markdown documentation. Every public type in Core **must** be documented; the `tools/validate-docs.ps1` script enforces this on every `build-and-deploy.bat` run.

## When to use this skill

Activate this skill when:

- Adding a new `class` / `record` / `struct` / `enum` / `interface` to `src/SmartCon.Core/`
- Renaming or deleting a documented type
- Reorganizing the `docs/domain/` structure
- Running or interpreting the validator
- Auditing orphan warnings from the validator
- Reviewing or writing the `build-and-deploy.bat` pre-step

## Layout

```
docs/domain/
├── README.md                       # index + rules (incl. split rule)
├── glossary.md                     # term dictionary (unchanged)
├── models/
│   ├── README.md                   # index of model files by module
│   ├── pipeconnect.md              # Core PipeConnect models
│   ├── project-management.md
│   ├── family-manager/             # large module -> subfolder (10 topic files)
│   │   ├── README.md               # topic index
│   │   ├── catalog.md
│   │   ├── attributes.md
│   │   ├── import.md
│   │   ├── load-placement.md
│   │   ├── type-catalog.md
│   │   ├── stale-detection.md
│   │   ├── content-hash.md
│   │   ├── actualization.md
│   │   ├── geometry.md
│   │   └── family-facts.md
│   ├── family-manager-rbac.md
│   ├── family-manager-loadable.md
│   ├── system-families.md
│   ├── formula-engine.md
│   ├── math-utilities.md
│   ├── updates.md
│   └── cross-cutting.md            # utilities, DTOs, serializers, log, common
└── interfaces/
    ├── README.md
    ├── pipeconnect.md
    ├── project-management.md
    ├── family-manager/             # large module -> subfolder (10 topic files)
    │   ├── README.md
    │   ├── catalog.md
    │   ├── import.md
    │   ├── documents-assets.md
    │   ├── load-placement.md
    │   ├── database.md
    │   ├── ui-dialogs.md
    │   ├── attributes.md
    │   ├── stale-detection.md
    │   ├── extraction.md
    │   └── actualization.md
    ├── family-manager-rbac.md
    ├── family-manager-loadable.md
    ├── family-manager-system.md
    ├── drag-drop-contracts.md
    ├── ui-contracts.md
    └── cross-cutting.md
```

**Rule of thumb:** file split mirrors the code's directory structure. If you add a new top-level subfolder under `Core/`, create a matching `models/<subfolder>.md` or `interfaces/<subfolder>.md` file.

## Split rule for large modules (1000-line threshold)

A documentation file **must not exceed 1000 lines**. The validator prints a `[WARN]` for every oversized file. When a module file approaches the threshold:

1. Create a subfolder `models/<module>/` (or `interfaces/<module>/`).
2. Split content into **topic files** (`catalog.md`, `import.md`, `stale-detection.md`, ...).
   One file = one coherent topic; target 100-600 lines per file.
3. Create `README.md` inside the subfolder with a topic-file index table.
   (`README.md` is excluded from heading parsing at any nesting level.)
4. Every topic file keeps the **same `module:` frontmatter** as the original file
   (e.g. all files under `models/family-manager/` use `module: family-manager`).
5. Topic files use H1 for the title (`# Модели FamilyManager — Импорт`) and `## TypeName`
   for types as usual. **Never** use `##` for topic-group headers — the validator
   treats every H2-H4 as a type name.
6. Delete the original `<module>.md` and update references in
   `models/README.md` (or `interfaces/README.md`) and other docs.
7. Run the validator → expect `PASSED` with no new warnings.

Worked example: `models/family-manager/` (2026-07) — one 2768-line file split into
10 topic files of 86-583 lines; interfaces mirror split into 10 files of 90-251 lines.

## File format

Each `.md` file (except `README.md`) starts with YAML frontmatter:

```markdown
---
module: pipeconnect
---
# Heading

> Загружать: при работе с ...
> Источник истины: `src/SmartCon.Core/...`.

## TypeName

Краткое описание назначения.

**Файл:** `Path/To/TypeName.cs`

```csharp
public sealed class TypeName
{
    public string Foo { get; init; }
}
```

---

## OtherType
...
```

### Heading rules

- Use `## TypeName` (level 2) for the primary type in a section
- Use `###` (level 3) for sub-sections (e.g. nested records, helper explanations)
- Multiple types per file are fine — use `---` separator
- A `## TypeA / TypeB` heading is allowed when documenting two related types in one section. The validator normalizes by taking the **first** name.

### Generic headers (not type names)

These are skipped by the validator. Don't put them as `##`:

- `Models`, `Interfaces`
- `Math Utilities`, `Math`
- `FamilyManager Models`, `FamilyManager Interfaces`
- `RBAC Models`, `RBAC Interfaces`
- `Drag & Drop Contracts`, `UI Contracts`
- `Cross-cutting`, `Cross-cutting Models`, `Cross-cutting Abstractions`, `Cross-cutting Utilities`
- `Updates`
- `DB Migration Models`, `DB Migration Abstractions`, `DB Migration Utilities`
- `System Families Models`, `System Families Interfaces`

## Classification rules (CRITICAL for the validator)

The validator classifies each `.cs` file in `SmartCon.Core/` by scanning for the **first** top-level type declaration whose name matches the file basename:

| File basename | First declaration | Classified as | Document in |
|---|---|---|---|
| `IFoo.cs` | `public interface IFoo` | interface | `docs/domain/interfaces/<module>.md` |
| `IFoo.cs` | `public interface IFoo` BUT file also has `public enum DialogResult` first | **interface** (primary type wins) | `docs/domain/interfaces/<module>.md` |
| `Foo.cs` | `public sealed class Foo` | model | `docs/domain/models/<module>.md` |
| `Foo.cs` | `public static class Foo` | model | `docs/domain/models/<module>.md` |
| `IFoo.cs` | file has only nested `public interface IFoo` (no top-level) | model (filename alone doesn't override missing declaration) | `docs/domain/models/<module>.md` |

**Important:** the validator uses the **primary type's directory**, not the filename's `I*` convention. So `IFittingCtcSetupItem.cs` (which lives in `SmartCon.Core/Models/`) is classified as a model — but only if the file's primary type IS `IFittingCtcSetupItem`. In our code it is, so the heading belongs in `docs/domain/models/pipeconnect.md` (already correct).

## The validator

### Running

```powershell
# From repo root
powershell -ExecutionPolicy Bypass -File tools\validate-docs.ps1

# Called automatically by build-and-deploy.bat step [0/10]
# Exit codes:
#   0 = PASSED
#   1 = FAILED (missing types in docs)
#   2 = STRUCTURAL ERROR (docs/domain/ not found, README missing)
```

### What it scans

1. **All** `.cs` files under `src/SmartCon.Core/`, excluding `bin/` and `obj/`
2. Each file is classified via `Test-IsInterfaceFile` (file content scan)
3. For each code type, looks for a matching heading (normalized name) in the corresponding `docs/domain/{models,interfaces}/<module>.md`

### What it parses

For each `.md` file under `docs/domain/{models,interfaces}/` (**recursively**, including module subfolders; `README.md` excluded at any level):
1. Splits by frontmatter (YAML between `---` markers) to extract `module:`
2. Walks lines, using a state machine to track `inFence` (skip content inside ` ``` `)
3. On heading `^(#{2,4})\s+(.+)$` extracts the text
4. Normalizes the name:
   - Strips emphasis: `**bold**`, `*italic*`, `_italic_`
   - Strips trailing tags: `*(Phase N)*`, `*(System Families)*`, etc.
   - Takes first part if split by `/`
   - Skips generic headers (see list above)
5. Classifies by the **first path segment** under `docs/domain/` (`models` vs `interfaces`) — so files inside `models/family-manager/` still count as `models`

### File size check (informational, not errors)

After orphan detection, the validator prints `[WARN]` for every parsed `.md` file
exceeding **1000 lines**. Such files must be split into a module subfolder
(see "Split rule for large modules"). This warning never fails the build.

### Orphan warnings (informational, not errors)

The validator prints `[WARN]` for documented types that don't exist in Core. These come from:

| Source | Example | Resolution |
|---|---|---|
| Types in non-Core assemblies | `IRevitUIContext` (in `SmartCon.Revit/`), `IFamilyManagerViewModelFactory` (in `SmartCon.FamilyManager/`) | **Leave as-is** — out of scope for Core scan (Clean Architecture: Core must not depend on Revit/FamilyManager) |
| Sub-headings (not a type) | `FamilyManagerServices Aggregate`, `IFamilyManagerAwaitableEvent.RaiseAsyncTask` | **Refactor to non-heading** (e.g. `**bold paragraph**` or note inside parent section) |
| Outdated docs (type was removed) | `FamilyMetadataFormat`, `FamilyMetadataMigrator` (removed in ADR-024 cleanup) | **Delete the section** |

> **Note:** As of the refactor that split nested types into separate files (one type per .cs file), the "Nested types in another file's primary type" source no longer applies. Every public type in Core has its own .cs file and is matched 1:1 with a documentation heading.

## Adding a new type

### Step-by-step

1. Create the `.cs` file in the right `SmartCon.Core/` subdirectory.
2. Identify the **module** (which subdir it lives in: `Models/PipeConnect` → `pipeconnect`; `Services/FamilyManager` → `family-manager`; `Common` → `cross-cutting`; etc.).
3. Identify whether the validator classifies it as **model** (default) or **interface** (if `I*` + primary type is `interface`).
4. Open or create the right doc file:
   - Small module → flat `docs/domain/{models,interfaces}/<module>.md`
   - Large module with subfolder (e.g. `family-manager`) → pick the matching **topic file**
     inside `docs/domain/{models,interfaces}/<module>/<topic>.md`. If no topic fits,
     add a new topic file AND register it in the subfolder's `README.md` index.
   - New module → create `<module>.md` (flat) and add a row to `docs/domain/{models,interfaces}/README.md`.
5. Add a `## TypeName` section with:
   - One-paragraph description (Russian is fine, English is fine, be consistent with neighbors)
   - `**Файл:** \`Path/To/TypeName.cs\``
   - The full C# signature in a code fence (records, classes, enums)
6. Check the file size: if the file now exceeds ~900 lines, apply the
   "Split rule for large modules" instead of letting it cross 1000.
7. Run the validator: `powershell -File tools\validate-docs.ps1` → expect `PASSED`.

### Decision: models/ or interfaces/?

Quick check: open the file. If the **primary type declaration** (the one whose name matches the file basename) is `interface IFoo`, it goes to `interfaces/`. Otherwise it goes to `models/`.

```csharp
// File: IFoo.cs — primary type IFoo, declaration is "interface" -> interfaces/
public interface IFoo { ... }

// File: IFoo.cs with extra helper enum — primary type still IFoo -> interfaces/
public enum SomeOtherThing { ... }   // not the primary type
public interface IFoo { ... }

// File: IFoo.cs where the interface is INSIDE a class — primary type is the CLASS -> models/
public class Container {
    public interface IFoo { ... }  // nested, not a top-level type
}
```

## Removing/renaming a type

1. Delete the `.cs` file (or rename).
2. Search `docs/domain/` for the type name (heading or filename reference).
3. Either:
   - Delete the `## OldName` section (if type is gone), OR
   - Update the section to reflect the new name and file path.
4. Update cross-references in `AGENTS.md`, `CONTRIBUTING.md`, `docs/README.md`, ADR files, plan files.
5. Run validator → expect `PASSED`. Any orphan warning for the old name should be gone.

## Multi-name headings

When documenting two related types in one section, the format is:

```markdown
## TypeA / TypeB
```

The validator normalizes by taking the first part (`TypeA`). If both types have separate `.cs` files, **TypeB will be flagged as orphan** — that's expected. To avoid the warning, either:
- Add a separate `## TypeB` section elsewhere
- Add an explicit `**См. также:** [TypeB](other-file.md)` link in the section

## When the validator gets it wrong

If the validator classifies a file as the wrong side (model vs interface), check the file's content:

- Is there a non-primary type declared first (before the one matching the filename)? The validator stops at the first match — if it's a non-matching type, the file is misclassified. **Fix the file** (reorder declarations) or split it.
- Is the filename `IFoo.cs` but the primary type is `public class Foo`? Either **rename the file** to `Foo.cs` (preferred) or **add an explicit `interface` declaration** in the file matching the basename.

## Test cases

```powershell
# Should pass with 0 ERROR and 0 structural errors
powershell -File tools\validate-docs.ps1

# Expected output characteristics:
# - "[2/4]" scans 30+ .md files (recursive, incl. models/family-manager/ and interfaces/family-manager/)
# - Documented name counts match Core type counts (models + interfaces)
# - No oversized-file warnings (all parsed files <= 1000 lines)

# Allowable warnings (all are "expected" per the table above):
# - SmartCon.Revit/ types
# - SmartCon.FamilyManager/ types
# - Sub-headings like "SmartConLogger Scopes", "RaiseAsyncTask", "FamilyManagerServices Aggregate"
# - Nested types in ILoadableFamilyImportOrchestrator.cs, SizeTableRow.cs, StatusMapping.cs, etc.

# Should never show:
# - A type from SmartCon.Core that's missing a heading
# - A heading name that doesn't match any .cs file in Core (unless it's a known orphan)
# - An oversized-file warning (means someone let a file grow past 1000 lines without splitting)
```

## Related files

- `tools/validate-docs.ps1` — the validator (PowerShell 5.1 compatible, no external deps)
- `docs/domain/README.md` — SSOT for the split rule (also mirrored in this skill)
- `build-and-deploy.bat:23-29` — calls validator at step `[0/10]`, **warning-only** (does NOT block deploy)
- `.github/PULL_REQUEST_TEMPLATE.md:24-26` — PR checklist mentions docs/domain/<module>.md
- `AGENTS.md:222-224` — rule "new domain class → update docs/domain/models/<module>.md"
- `CONTRIBUTING.md:160-161` — same rule in English

## Maintenance

When changing the validator (`tools/validate-docs.ps1`):
1. Add a test case to this skill (in "Test cases" section)
2. Update "Classification rules" if behavior changed
3. Update "What it scans" / "What it parses" if scope changed
4. Re-run on the whole repo to catch regressions
5. Update the docs/ README files to point to the new structure (if files added/removed)
