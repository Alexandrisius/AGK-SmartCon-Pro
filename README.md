# SmartCon — Intelligent MEP Connector for Autodesk Revit

[![.NET 8](https://img.shields.io/badge/.NET-8.0-blue)](https://dotnet.microsoft.com/)
[![Revit 2025](https://img.shields.io/badge/Revit-2025-green)](https://www.autodesk.com/products/revit/)
[![C# 12](https://img.shields.io/badge/C%23-12-purple)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

SmartCon is a **Revit plugin** that automates routine MEP (Mechanical, Electrical, Plumbing) pipe connection operations. Its flagship module — **PipeConnect** — lets you connect pipe elements in any 3D view with just **two clicks**, with automatic fitting selection, parameter resolution, and connection type matching.

## The Problem

Revit lacks convenient tools for connecting pipe elements in 3D views:
- You have to manually drag connectors and hope coordinates align in 3D space
- No system for connection types (threaded / welded / socket / press)
- No smart fitting/reducer selection based on connector compatibility
- Chain connections (pipe networks) require tedious element-by-element work

## The Solution

**PipeConnect** solves all of these with a two-click workflow:

1. **Click 1** — Select the element to move (dynamic)
2. **Click 2** — Select the stationary target (static)
3. SmartCon automatically:
   - Aligns the dynamic element to the static connector (position + rotation)
   - Matches connection types and selects the appropriate fitting
   - Resolves sizes through lookup tables and formula analysis
   - Inserts reducers when diameters don't match
   - Supports chain connections (entire pipe networks)

## Architecture

```
SmartCon.Core          — Pure C#: models, interfaces, algorithms (testable without Revit)
SmartCon.Revit         — Revit API implementations of Core interfaces
SmartCon.UI            — Shared WPF styles and controls
SmartCon.App           — Entry point: IExternalApplication, Ribbon, DI container
SmartCon.PipeConnect   — PipeConnect module: Commands, ViewModels, Views
SmartCon.Tests         — Unit tests (xUnit + Moq)
```

Key architectural decisions:
- **Clean Architecture** with dependency inversion — Core defines interfaces, Revit implements them
- **MVVM** with CommunityToolkit.Mvvm source generators
- **TransactionGroup + Assimilate** pattern for single-undo operations
- **Formula Engine** for parsing and solving Revit family parameter formulas

See [`docs/architecture/`](docs/architecture/) for detailed architecture documentation.

## Prerequisites

- **Autodesk Revit 2025**
- **.NET 8.0 SDK** (for building)
- **Visual Studio 2022** or **Rider** (recommended)

## Getting Started

```bash
# Clone the repository
git clone https://github.com/Alexandrisius/AGK-SmartCon-Pro.git
cd AGK-SmartCon-Pro

# Build
dotnet build src/SmartCon.sln

# Run tests
dotnet test src/SmartCon.sln

# Deploy to Revit (copies to Addins folder)
build-and-deploy.bat
```

After deployment, restart Revit. SmartCon appears in the **Add-Ins** ribbon tab.

## Project Structure

```
├── docs/                 — Developer documentation (SSOT)
│   ├── README.md         — Documentation index
│   ├── invariants.md     — Hard rules (I-01..I-10)
│   ├── architecture/     — Solution structure, dependency rules, tech stack
│   ├── domain/           — Models, interfaces, glossary
│   ├── pipeconnect/      — State machine, algorithms, UI spec
│   └── adr/              — Architecture Decision Records
├── src/
│   ├── SmartCon.sln
│   ├── SmartCon.Core/    — Pure domain logic
│   ├── SmartCon.Revit/   — Revit API layer
│   ├── SmartCon.UI/      — WPF shared resources
│   ├── SmartCon.App/     — Plugin entry point + DI
│   ├── SmartCon.PipeConnect/ — PipeConnect module
│   └── SmartCon.Tests/   — Unit tests
└── .editorconfig         — Code style rules
```

## Tech Stack

| Component | Technology |
|---|---|
| Runtime | .NET 8.0 (net8.0-windows) |
| Language | C# 12 |
| UI Framework | WPF |
| MVVM | CommunityToolkit.Mvvm (source generators) |
| Testing | xUnit + Moq |
| Revit API | Revit 2025 |

## Contributing

Contributions are welcome! Please read the [invariants](docs/invariants.md) and [dependency rules](docs/architecture/dependency-rule.md) before submitting changes.

Key rules:
- **I-01**: Revit API from WPF only via `IExternalEventHandler`
- **I-03**: Transactions only through `ITransactionService`
- **I-09**: `SmartCon.Core` references RevitAPI.dll compile-time only — no API calls
- **I-10**: Strict MVVM — code-behind contains only `DataContext = viewModel`

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.

## Author

**AGK** — MEP automation tools for Autodesk Revit
