---
name: smartcon-build-guide
description: "SmartCon Revit plugin build guide. Multi-version build configurations (R19/R21/R24/R25/R26/R27), correct restore/build commands, CI/CD workflow, branch protection. Triggers on: build, deploy, release, compile, dotnet build, configuration. Keywords: SmartCon, Revit, build, deploy, release, R19, R21, R24, R25, R26, R27, net48, net8, net10, restore, CI/CD."
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
| `Debug.R27` | 2027 | net10.0-windows | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27` |
| `Debug.R26` | 2026 | net8.0-windows | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26` |
| `Debug.R25` | 2025 | net8.0-windows | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` |
| `Debug.R24` | 2024 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24` |
| `Debug.R21` | 2021-2023 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21` |
| `Debug.R19` | 2019-2020 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19` |

**Tests:** `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25`
(the Tests project is pinned to net8.0-windows — do NOT build it under R27: NU1201/NU1202;
in the sln its R27 configurations are mapped to R25, honored only in VS)

**SDK:** `global.json` pins SDK 10.0.100 (rollForward latestPatch) — ALL configurations
(including net48) build under SDK 10. The .NET 8 RUNTIME is still required to run unit tests.

## Build vs Deploy

### Intermediate Build (Development/Testing)
Build individual configurations for quick testing during development. Does NOT deploy to Revit.
```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26
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
# Build ALL 6 shipping configurations (for verification, NOT deploy)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26
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

### 3. TFM Switch = NEVER Use `--no-restore`

`Nice3point.Revit.Api.RevitAPI` is pulled with `VersionOverride` that depends
on `$(RevitVersion)`. Each configuration must produce a fresh
`obj/project.assets.json` that targets the right Revit API year (2021.*,
2022.*, 2025.*, 2027.*). Once an `assets.json` is written, it is reused on the
next build **regardless of the new `-c` flag** unless you re-restore.

**This is why `build-and-deploy.bat` runs `dotnet build` WITHOUT
`--no-restore` for every configuration** (lines 29, 35, 41, 47): the
restore step is part of the build, and produces a clean assets file.

```bash
# WRONG — assets.json from previous config is reused, R21 build
# sees the R25 RevitAPI and explodes with CS0246 ForgeTypeId (added
# in Revit 2022, not in 2021.*).
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21 --no-restore
#                            ^^^^^^^^^^^^^^^^^^^^^^ NEVER do this

# WRONG — separate restore of a different config writes assets.json
# for that config, then subsequent --no-restore build of yet another
# config reuses the wrong assets.
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21 --no-restore

# CORRECT — restore is part of every build, just like build-and-deploy.bat
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19
```

**Heuristic for the agent:** if you are about to run
`dotnet build … --no-restore`, you are doing it wrong. The only time
`--no-restore` is acceptable is the SECOND build of the **same**
configuration in a row (e.g. after fixing a typo in a source file).
Switching `-c` always requires a fresh restore.

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
