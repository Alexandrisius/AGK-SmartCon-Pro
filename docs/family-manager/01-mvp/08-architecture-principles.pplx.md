# FamilyManager Architecture Principles

## Цель документа

Документ фиксирует архитектурные правила, которые должны направлять технический MVP plan и реализацию.

## Principle 1: smartCon Dependency Rule Is Non-Negotiable

`SmartCon.FamilyManager` depends only on:

- `SmartCon.Core`;
- `SmartCon.UI`.

It must not depend on:

- `SmartCon.Revit`;
- `SmartCon.App`;
- concrete Revit implementations.

## Principle 2: Revit API Is Behind Core Interfaces

All Revit operations must be declared as interfaces in `SmartCon.Core/Services/Interfaces` and implemented in `SmartCon.Revit`.

Examples:

- `IFamilyLoadService`;
- `IFamilyMetadataExtractionService`;
- `IProjectFamilyUsageRepository`;
- `IFamilyDocumentService`.

## Principle 3: Catalog Lives In Database, Not In Revit Storage

FamilyManager has two main storage scopes:

| Scope | Storage | Meaning |
| --- | --- | --- |
| User/local catalog | SQLite + file cache | Library of available BIM content |
| Corporate catalog | Server DB + object storage | Organization BIM content |
| Project usage history | Local DB in MVP; server DB in corporate phase | Which catalog item/version was loaded or used |

Do not store catalog records, family metadata, versions, tags, previews, search index or usage history in ExtensibleStorage.

For MVP, do not create `FamilyManagerSchema.cs` in `SmartCon.Revit/Storage`; FamilyManager storage is database-first.

## Principle 4: Provider Abstraction Comes Before Server

MVP implements only local provider, but UI and application services must speak to provider contracts.

This prevents rewriting UI when server provider appears.

## Principle 5: Revit Threading Rules Win Over Async Convenience

No Revit API calls from:

- background thread;
- ViewModel `Task.Run`;
- SQLite indexing task;
- arbitrary async callback.

Background work can hash files and update SQLite, but Revit API extraction/loading must go through Revit-safe execution path.

## Principle 6: Minimal Dependency Footprint

Client MVP dependencies should be limited to:

- existing smartCon stack;
- `Microsoft.Data.Sqlite 8.x`;
- optional Dapper;
- optional simple migration helper written in-house.

Avoid:

- EF Core in Revit client;
- UI frameworks;
- reactive frameworks;
- logging frameworks;
- server libraries.

## Principle 7: Immutable Domain, Mutable ViewModels

Domain records in Core should be immutable snapshots.

WPF row models can be mutable `ObservableObject` classes in `SmartCon.FamilyManager/ViewModels`.

## Principle 8: No Live Revit Objects in UI State

ViewModels must not store:

- `Document`;
- `Element`;
- `Family`;
- `FamilySymbol`;
- `Connector`.

If absolutely required, pass `Document` as opaque parameter through a service boundary; never call it from Core or UI.

## Principle 9: Migrations Are Explicit and Testable

SQLite schema migrations:

- run on startup/open catalog;
- are idempotent;
- are covered by tests;
- backup before destructive operations;
- never silently delete user data.

Server database migration:

- versioned through explicit DB migrations;
- backward compatibility rules defined before remote provider;
- no hidden migration from Revit project storage.

## Principle 10: Enterprise Features Stay Behind Boundaries

The following must not leak into MVP UI as hard dependencies:

- SSO;
- RBAC;
- OpenSearch;
- object storage SDK;
- server sync engine;
- approval workflows.

They can exist as future capabilities in provider contract and domain lifecycle.

## Repository-Specific Constraints

| Constraint | FamilyManager Impact |
| --- | --- |
| `TreatWarningsAsErrors=true` | No warning debt |
| Central Package Management | New packages go through `Directory.Packages.props` |
| MEDI 8.x ceiling | Do not upgrade `Microsoft.Extensions.*` above 8.x in client |
| STJ 8.x | Use `System.Text.Json`, no Newtonsoft |
| `ElementIdCompat` | Serialize IDs as `long` |
| I-12 | Programmatic DataGrid headers |
| I-13 | Existing smartCon per-project settings pattern; FamilyManager catalog still lives in DB, not ExtensibleStorage |
| CI matrix | R19/R21/R24/R25/R26 must remain green |
