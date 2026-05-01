# FamilyManager Risk Register and ADR Backlog

## Цель документа

Документ фиксирует ключевые риски и архитектурные решения, которые нужно принять до и во время реализации MVP.

## Risk Register

| ID | Risk | Impact | Mitigation |
| --- | --- | --- | --- |
| R-01 | `EditFamily` нельзя вызывать при `doc.IsModifiable=true` | Load/extract fails | Проверять перед operation; Revit layer only |
| R-02 | Family document transaction lifecycle сложен | Family не перезагрузится | Следовать pattern `tx.Dispose()` before `LoadFamily` |
| R-03 | Revit API нельзя вызывать из background thread | Crashes/undefined behavior | ExternalEvent/main thread only |
| R-04 | Legacy API differences | Broken R19/R21/R24 builds | `#if` только в разрешённых слоях |
| R-05 | ElementId 32/64-bit differences | Bad serialization | `ElementIdCompat`, serialize long |
| R-06 | DataGrid header localization on net48 | Empty headers | Programmatic headers |
| R-07 | SQLite native assets packaging | Add-in load failure | Smoke build/install on R25/R24 |
| R-08 | EF Core/client dependency bloat | Dependency conflicts | Use Microsoft.Data.Sqlite/Dapper/manual SQL |
| R-09 | Local DB lost during update | User data loss | Store outside install folder; document updater rule |
| R-10 | Случайная попытка хранить каталог в ExtensibleStorage | Неверная архитектура и плохая масштабируемость | Каталог только в локальной/серверной БД |
| R-11 | Неправильная идентификация проекта для usage history | Неполная история использования | Project identity strategy в SQLite/server DB |
| R-12 | Import folder freezes UI | Poor UX | Progress + cancellation + background file hashing |
| R-13 | Large family metadata extraction slow | Bad performance | Two-phase indexing: file metadata first, deep later |
| R-14 | Missing original files | Broken load | Cached mode or Missing state |
| R-15 | Duplicate families confuse users | Bad catalog quality | SHA-256 duplicate detection |
| R-16 | Search quality too weak | Low adoption | Normalized tokens now, FTS5 later |
| R-17 | Server needs leak into MVP | Scope explosion | Provider abstraction only |
| R-18 | Credentials mishandled later | Security issue | Credential abstraction before remote provider |
| R-19 | IP/corporate content leakage | Enterprise blocker | No upload in MVP; future visibility model |
| R-20 | Preview generation too expensive | Delays MVP | Preview unavailable state acceptable |
| R-21 | Incorrect domain terms | Rewrite risk | Domain model document before technical plan |
| R-22 | Project usage and catalog ownership mixed | Bad architecture | Usage history belongs to local/server DB |

## Risk Priorities

| Priority | Risks |
| --- | --- |
| Highest | R-01, R-03, R-07, R-09, R-10, R-22 |
| High | R-04, R-05, R-11, R-12, R-13, R-14 |
| Medium | R-06, R-08, R-15, R-16, R-17 |
| Future | R-18, R-19 |

## ADR Backlog

### ADR-FM-001: FamilyManager module boundary

Decision:

- new `SmartCon.FamilyManager` project;
- references only Core/UI;
- Revit implementations stay in `SmartCon.Revit`.

### ADR-FM-002: Dockable panel lifecycle

Decision needed:

- dockable panel in MVP or modal fallback;
- content lifetime;
- active document switching;
- ExternalEvent usage.

### ADR-FM-003: Local SQLite catalog

Decision:

- canonical database root: `%APPDATA%\AGK\SmartCon\FamilyManager\`;
- database file: `familymanager.db`;
- package: `Microsoft.Data.Sqlite 8.x`;
- MVP migration state: `schema_info` with `schema_version`;
- backup before destructive migration.

### ADR-FM-004: File cache strategy

Decision needed:

- Linked vs Cached vs Hybrid;
- cache path;
- cleanup;
- missing file behavior.

### ADR-FM-005: Metadata extraction levels

Decision needed:

- shallow import fields;
- deep extraction fields;
- what requires Revit API;
- what can run outside Revit API.

### ADR-FM-006: Provider abstraction

Decision needed:

- provider capabilities;
- read/write contracts;
- error model;
- remote readiness.

### ADR-FM-007: Project usage database storage

Decision needed:

- usage history table in local DB;
- project identity strategy;
- server-side usage model for corporate phase;
- rule that MVP does not use ExtensibleStorage for catalog or usage history.

### ADR-FM-008: Search strategy

Decision needed:

- MVP normalized SQL search;
- FTS5 timing;
- fuzzy matching scope.

### ADR-FM-009: Security and credentials

Decision needed:

- no credentials in MVP;
- future Windows Credential Manager;
- log redaction.

### ADR-FM-010: Legacy mode

Decision needed:

- what works in R19/R21/R24;
- what is disabled;
- how UI communicates limitations.

## ADR Timing

| Before coding | During MVP | Before remote phase |
| --- | --- | --- |
| FM-001, FM-003, FM-004, FM-006, FM-007 | FM-002, FM-005, FM-008, FM-010 | FM-009 plus server ADRs |
