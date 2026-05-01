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

```bash
# Revit 2025 (net8.0-windows)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# Revit 2024 (net48, separate artifact)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24

# Revit 2021-2023 (net48)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21

# Revit 2019-2020 (net48)
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

All 841 tests must pass before submitting a PR. Tests that require a live
Revit runtime are automatically skipped in CI.

## Automation Overview

- **GitHub Actions `build.yml`** — CI validation for `push` / `pull_request`. It checks that the repository still builds and tests pass.
- **GitHub Actions `release.yml`** — GitHub-side release automation on tag `v*`. Intended for open-source distribution and GitHub Releases.
- **`build-and-deploy.bat`** — local developer script for debug build + deploy into locally installed Revit versions.
- **`tools/release.ps1`** — maintainer release script. This is the primary local release workflow: version bump, build, tests, publish, ZIP, optional installer, git tag, GitHub release.

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
SmartCon.App --> PipeConnect --> SmartCon.Revit --> SmartCon.Core
                                      \-> SmartCon.UI   \-> SmartCon.Core
App --> ProjectManagement
App --> FamilyManager --> SmartCon.Core
                     \-> SmartCon.UI
```

- **SmartCon.Core** — Pure C#, no Revit API calls, no WPF. Domain models, interfaces, algorithms.
- **SmartCon.Revit** — Revit API implementations of Core interfaces.
- **SmartCon.UI** — Shared WPF styles and controls.
- **SmartCon.PipeConnect** — Module-specific commands, ViewModels, Views.
- **SmartCon.FamilyManager** — dockable panel, SQLite catalog, family management.
- **SmartCon.App** — Entry point: `IExternalApplication`, Ribbon, DI container.

Key invariants (see `docs/invariants.md`):
- Revit API from WPF — only through `IExternalEventHandler`
- Transactions — only through `ITransactionService`
- Never store `Element`/`Connector` between transactions; use `ElementId`

## Adding Code

- New domain classes → update `docs/domain/models.md`
- New interfaces → update `docs/domain/interfaces.md`
- Architectural decisions → create ADR in `docs/adr/`
- Follow MVVM strictly in UI: `.xaml.cs` contains only `DataContext = viewModel`

## Pull Requests

- Keep PRs focused on a single concern
- Include tests for new logic in `SmartCon.Core`
- Ensure `dotnet build` and `dotnet test` pass
- Reference related issues in the PR description
