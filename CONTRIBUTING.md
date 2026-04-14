# Contributing to SmartCon

Thank you for your interest in contributing to SmartCon — a Revit plugin for automating MEP operations.

## Build

```bash
dotnet restore src/SmartCon.sln
dotnet build src/SmartCon.sln
```

Requirements:
- .NET 8 SDK
- Revit 2025 installed (for RevitAPI.dll reference at design-time)

## Test

```bash
dotnet test src/SmartCon.sln --no-restore
```

All tests must pass before submitting a PR. Tests that require the Revit runtime are automatically skipped in CI.

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
SmartCon.App → SmartCon.PipeConnect → SmartCon.Revit → SmartCon.Core
                                     ↘ SmartCon.UI   ↘ SmartCon.Core
```

- **SmartCon.Core** — Pure C#, no Revit API calls, no WPF. Domain models, interfaces, algorithms.
- **SmartCon.Revit** — Revit API implementations of Core interfaces.
- **SmartCon.UI** — Shared WPF styles and controls.
- **SmartCon.PipeConnect** — Module-specific commands, ViewModels, Views.
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
