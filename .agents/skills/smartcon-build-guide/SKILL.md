---
name: smartcon-build-guide
description: "SmartCon Revit plugin build guide. Multi-version build configurations (R19/R21/R24/R25), correct restore/build commands, CI/CD workflow, branch protection. Triggers on: build, deploy, release, compile, dotnet build, configuration. Keywords: SmartCon, Revit, build, deploy, release, R19, R21, R24, R25, net48, net8, restore, CI/CD."
license: MIT
metadata:
  author: AGK Engineering
  version: "1.0.0"
---

# SmartCon Build & Deploy Guide

Multi-version Revit plugin build configurations and CI/CD workflow.

## Quick Reference

| Config | Revit | TFM | Command |
|---|---|---|---|
| `Debug.R25` | 2025-2026 | net8.0-windows | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` |
| `Debug.R24` | 2024 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24` |
| `Debug.R21` | 2021-2023 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21` |
| `Debug.R19` | 2019-2020 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19` |

**Tests:** `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25`

## Build vs Deploy

### Intermediate Build (Development/Testing)
Build individual configurations for quick testing during development. Does NOT deploy to Revit.
```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19
```

### Full Build + Deploy (Production Ready)
**ONLY use after user has manually tested changes in Revit.** This builds ALL configurations AND copies files to Revit addins folders.
```bash
build-and-deploy.bat
```
**WARNING:** Do NOT run `build-and-deploy.bat` during development. Use individual `dotnet build` commands instead.

## CRITICAL Rules (Agents Forget These!)

### 1. Build ALL Versions, Not Just R25
When making changes that affect compilation (new APIs, #if directives, project files):
```bash
# Build ALL 4 configurations (for verification, NOT deploy)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19

# OR for final deploy (only after user testing!)
build-and-deploy.bat
```

### 2. Restore Requires Configuration
```bash
# WRONG — fallback to RevitAPI 2021.*, false errors CS0618
dotnet restore src/SmartCon.App/SmartCon.App.csproj

# CORRECT — configuration tells restore which RevitAPI version
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R25
```

### 3. TFM Switch = Force Restore
When switching between net8 and net48 configurations:
```bash
# net8 → net48: MUST restore with config
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R24

# net48 → net8: MUST restore with config
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R25
```

### 4. Build App Project, NOT Solution
```bash
# WRONG — pulls extra TFM
dotnet build src/SmartCon.sln -c Debug.R25

# CORRECT
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
```

## CI/CD

- **Branch protection:** `main` requires PR → squash-merge. NEVER push to main directly.
- **NO PR without user request:** Agent commits to current branch only. PR only if user says "merge to main".
- **Dependabot frozen packages:** `Microsoft.Extensions.DependencyInjection` 8.x, `System.Text.Json` 8.x
- **Release:** `tools\release.bat` (local only, CI validates tags)

## References

- [Build Configurations](references/build-configurations.md) — Full multi-version build guide
- [CI/CD Workflow](references/ci-cd-workflow.md) — GitHub Actions, branch protection, release
