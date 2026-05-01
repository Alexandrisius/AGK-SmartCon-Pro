# Contributing to SmartCon

Thank you for your interest in contributing to SmartCon — a Revit plugin for automating MEP operations.

## Build

SmartCon supports **eight Revit versions** (2019, 2020, 2021, 2022, 2023, 2024,
2025, 2026) with two target frameworks (`net48` and `net8.0-windows`). The project
uses **named build configurations** (`Debug.R25`, `Debug.R24`, `Debug.R21`, `Debug.R19`)
to select the correct RevitAPI NuGet package **before** NuGet restore runs.

Shipping artifacts are grouped as follows:

- `R19` → Revit 2019-2020
- `R21` → Revit 2021-2023
- `R24` → Revit 2024
- `R25` → Revit 2025-2026

> ⚠️ Do **not** use `-p:RevitVersion=...`. The
> `Nice3point.Revit.Api.RevitAPI` package resolves its `VersionOverride`
> during restore, and command-line properties are not visible at that stage.
> Use named configurations instead — they are parsed in
> `Directory.Build.props`.

### Full multi-version build (recommended)

```bash
build-and-deploy.bat
```

This builds R25 / R24 / R21 / R19 + the updater, and deploys artifacts to the Revit
add-ins directory.

### Single-version build

When switching between different TFMs (`net8.0-windows` ↔ `net48`), run
`dotnet restore` first — the RevitAPI NuGet package has different versions per TFM.

```bash
# Revit 2025 (net8.0-windows)
dotnet restore src/SmartCon.App/SmartCon.App.csproj
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# Revit 2024 (net48, separate artifact) — restore required after net8!
dotnet restore src/SmartCon.App/SmartCon.App.csproj
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24

# Revit 2021-2023 (net48) — same TFM, no restore needed
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21

# Revit 2019-2020 (net48) — same TFM, no restore needed
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19
```

Always target `SmartCon.App.csproj`, **not** `SmartCon.sln` — the solution
pulls in test projects and may conflict with named configurations.

Requirements:
- .NET 8 SDK
- Windows (WPF)
- Revit is **not** required to build — RevitAPI.dll comes from NuGet
  (`Nice3point.Revit.Api`) at design-time.

## Test

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

All ~940 tests must pass before submitting a PR. Tests that require a live
Revit runtime are automatically skipped in CI.

## Automation Overview

### GitHub Actions

| Workflow | Trigger | Purpose |
|---|---|---|
| `build.yml` | push to `main`, PR, tags `v*` | CI: build 5 configs (R19/R21/R24/R25/R26) + run tests |
| `codeql.yml` | push/PR to `main`, weekly Monday | Security scanning (C#) |
| `stale.yml` | daily at 04:00 UTC | Auto-close inactive issues/PRs after 30 days |

### Local scripts

| Script | Purpose |
|---|---|
| `build-and-deploy.bat` | Debug build of all versions + deploy to locally installed Revit |
| `tools/release.ps1` | Maintainer release: version bump, build, test, publish, ZIP, optional Inno Setup installer, git tag, push, GitHub Release via `gh` CLI |

Releases are created **locally** by `tools/release.ps1` — there is no CI-side
release workflow. The `build.yml` workflow only validates the build on tags `v*`.

## Code Style

Project follows the settings in `.editorconfig` at repo root. Key points:

- **File-scoped namespaces** (`namespace Foo;`, not block-scoped)
- **Private fields:** `_camelCase`
- **Constants and public members:** `PascalCase`
- **Interfaces:** `IPascalCase`
- **Indentation:** 4 spaces for C#, 2 spaces for XML/JSON/XAML
- **Line endings:** CRLF
- **Braces:** on new line for all blocks
- **Seal classes** that are not designed for inheritance
- **No `this.` qualifier** unless necessary to disambiguate
- **Remove unused `using` directives**

Run the build with warnings-as-errors mindset. The analyzer rules in `.editorconfig` define the enforced baseline.

## Branch Naming

- `feature/<short-description>` — new functionality
- `fix/<short-description>` — bug fixes
- `refactor/<short-description>` — code reorganization
- `docs/<short-description>` — documentation changes

## Commit Messages

Use concise, imperative-style descriptions:

```
Add lookup table CSV parser
Fix connector alignment tolerance
Refactor transaction service interface
```

## Architecture

SmartCon follows a layered architecture with strict dependency rules:

```
SmartCon.App ──> SmartCon.PipeConnect ──> SmartCon.Revit ──> SmartCon.Core
              ──> SmartCon.ProjectManagement               \-> SmartCon.UI
              ──> SmartCon.FamilyManager
              ──> SmartCon.Updater (standalone net8.0, no Revit dependency)
```

- **SmartCon.Core** — Pure C#, no Revit API calls, no WPF. Domain models, interfaces, algorithms.
- **SmartCon.Revit** — Revit API implementations of Core interfaces.
- **SmartCon.UI** — Shared WPF styles and controls.
- **SmartCon.PipeConnect** — PipeConnect module: Commands, ViewModels, Views.
- **SmartCon.ProjectManagement** — Share Project module (ISO 19650): Commands, ViewModels, Views.
- **SmartCon.FamilyManager** — FamilyManager module: dockable panel, SQLite catalog, family management.
- **SmartCon.App** — Entry point: `IExternalApplication`, Ribbon, DI container.
- **SmartCon.Updater** — Standalone .NET 8 updater: applies pending update when Revit closes.

Key invariants (see `docs/invariants.md`, I-01 through I-16):
- Revit API from WPF — only through `IExternalEventHandler`
- Transactions — only through `ITransactionService`
- Never store `Element`/`Connector` between transactions; use `ElementId`
- Core must never call Revit API methods (only use value types as carriers)
- MVVM strictly: `.xaml.cs` contains only `DataContext = viewModel`

## Adding Code

- New domain classes → update `docs/domain/models.md`
- New interfaces → update `docs/domain/interfaces.md`
- Architectural decisions → create ADR in `docs/adr/`
- Follow MVVM strictly in UI: `.xaml.cs` contains only `DataContext = viewModel`

## Pull Requests

- Keep PRs focused on a single concern
- Include tests for new logic in `SmartCon.Core`
- Ensure all CI checks pass: build (R19/R21/R24/R25/R26) + tests
- Reference related issues in the PR description

### CI checks required for merge

The `main` branch is protected. All PRs require:
- ✅ 5 build matrix jobs pass (R19, R21, R24, R25, R26)
- ✅ Test job passes
- ✅ 1 approval from CODEOWNERS
- Linear history (no merge commits)
