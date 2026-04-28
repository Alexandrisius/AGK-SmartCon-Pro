# FamilyManager NFR and QA Strategy

## Цель документа

Документ задаёт нефункциональные требования и стратегию проверки MVP.

## Non-Functional Requirements

### Compatibility

| Requirement | Target |
| --- | --- |
| Primary runtime | Revit 2025+ / `net8.0-windows` |
| Legacy runtime | Revit 2019–2024 / `net48` |
| Build configs | R19/R21/R24/R25/R26 |
| Main API target | RevitAPI 2025 for MVP behavior |

Revit 2025 API uses .NET 8, while legacy Revit versions use .NET Framework 4.8 in the current smartCon compatibility model ([Autodesk Development Requirements](https://help.autodesk.com/view/RVT/2025/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Welcome_to_the_Revit_Platform_API_Development_Requirements_html)).

### Performance

| Scenario | MVP Target |
| --- | --- |
| Open panel | No visible Revit startup slowdown |
| Search 5,000 records | Interactive response |
| Import 100 files | Progress visible, no full UI freeze |
| Load one family | Revit operation completes or fails with clear error |
| SQLite queries | Indexed fields for common filters |

### Reliability

| Requirement | Target |
| --- | --- |
| Corrupted `.rfa` | Skipped with error, batch continues |
| Missing file | Item marked Missing, UI offers locate/reindex |
| SQLite migration failure | Backup/recovery path |
| SQLite catalog failure | Backup/recovery path, no silent data loss |
| Revit API exception | Log + user-facing error |

### Upgrade Safety

- Local catalog survives smartCon update.
- SQLite migrations are versioned.
- Cache layout is documented.
- Local SQLite schema migrations are stable and tested.
- Payload schema version supports backward reads.

### UX

- Long operations show progress.
- Cancel exists where safe.
- No active document state is clear.
- Disabled actions explain why.
- Deprecated/Archived items are visually distinct and text-labeled.

## QA Pyramid

| Layer | Tests |
| --- | --- |
| Unit | Domain models, normalization, search scoring, serializers |
| SQLite integration | Repository CRUD, migrations, duplicate hash, search |
| ViewModel | Commands, selection, validation, empty states |
| Revit smoke | Open UI, load family, write usage history to DB |
| Build matrix | R19/R21/R24/R25/R26 |
| Manual UX | Import folder, search, load, missing file |

## Required Test Fixtures

| Fixture | Purpose |
| --- | --- |
| Small valid `.rfa` | Basic import/load |
| Family with multiple types | Type extraction |
| Family with many parameters | Metadata performance |
| Duplicate file | Hash detection |
| Missing file path | Missing state |
| Corrupted file | Error handling |
| Cyrillic path | Localization/path robustness |
| Old Revit family | Compatibility behavior |

## Minimum Test Count

MVP should add at least:

| Area | Count |
| --- | --- |
| Domain/normalization | 10 |
| Metadata schema/serializers | 10 |
| SQLite repository/migrations | 15 |
| Search/filtering | 10 |
| ViewModel commands | 10 |
| Project usage repository | 5 |

Target: **60+ tests**. Minimum acceptable: **50+ tests**.

## CI Acceptance

Before MVP can be considered releasable:

- existing tests pass;
- new tests pass;
- `Debug.R25` test run passes;
- R19/R21/R24/R25/R26 builds pass;
- no warnings due `TreatWarningsAsErrors`;
- docs updated;
- ADRs added;
- manual smoke in Revit 2025 completed.

## Manual Smoke Checklist

1. Install smartCon build in Revit 2025.
2. Open Revit project.
3. Open FamilyManager from Ribbon.
4. Create/select local catalog.
5. Import one `.rfa`.
6. Import folder with at least one invalid file.
7. Search by name.
8. Filter by status.
9. Open details.
10. Load family to project.
11. Save project.
12. Reopen project and verify usage history in catalog DB.
13. Run same build on legacy Revit and verify graceful behavior.
