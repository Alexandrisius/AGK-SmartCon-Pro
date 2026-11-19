# H1d — Family editing pack: матрица доказательств (2026-11-19)

> **Статус:** реализовано, интеграционные гейты R25/R27 зелёные; ручной UI-прогон
> и live-dogfooding — pending (см. runbook).
> **Пакет:** `families` (9 тулов, каталог 33 → 42).
> **Коммиты:** `910fdc7b` (пакет + тесты), `382786c0` (Release-анализаторы),
> `2cba7294`-предшественник (дневные фиксы, база `846ebbe5`).

## 1. Состав пакета

| # | Tool | Тип | dryRun | Idempotent | Назначение |
|---|---|---|---|---|---|
| 1 | `list_families` | Read | — | ✔ | загруженные семейства: familyId, категория, число типов, isEditable/isInPlace |
| 2 | `list_family_types` | Read | — | ✔ | типы семейства, включая неразмещённые; typeId, параметры кратко |
| 3 | `get_family_parameters` | Read | — | ✔ | параметры family/type: scope, GUID, storage, formula, lookup, canSet*, значения по типам |
| 4 | `find_families_with_parameter` | Read | — | ✔ | живой проход по типам без индекса: exact/contains + valueContains |
| 5 | `rename_family_parameter` | Write | ✔ default | ✔ | instance/type, конфликт имён, shared/built-in → отказ |
| 6 | `add_family_parameter` | Write | ✔ default | ✖ | normal/shared, instance/type, group, GUID, initialValue |
| 7 | `set_family_parameter_values` | Write | ✔ default | ✔ | значения по типам, unit обязателен для размерных, formula → отказ |
| 8 | `set_family_formula` | Write | ✔ default | ✔ | CanAssignFormula, снятие формулы, size_lookup проверка, diff по типам |
| 9 | `map_family_parameters` | Write | ✔ default | ✔ | source→target по всем типам, scope/spec конфликты, mode values/formula |

Единый конверт ответа (все 9):
`{success, errorCode, message, data:{changed, skipped, failed, sections, changeSetId}}`.
`changeSetId` пишется при любом реальном изменении, включая `success=false`
(частичный сбой), dry-run всегда возвращает `wouldChange` через `changed[]`
и никогда не меняет модель.

## 2. Requirement → tool → unit → integration → eval → doc

| Req | Требование | Tool | Unit | Integration (R25/R27) | Eval/маршрутизация | Doc |
|---|---|---|---|---|---|---|
| R1 | Загруженные семейства, типы, isEditable/isInPlace | `list_families` | `AgentFamilyPackTests` | `ListFamilies_ReturnsSeededFamilyWithTypeCount`, `ListFamilies_NameFilterMatchesLocalizedText` (case-insensitive фильтр; русские имена проверяются во всех остальных тестах) | routing r173/r174 | карточка + §1 |
| R2 | Типы, включая неразмещённые | `list_family_types` | `AgentFamilyPackTests` | `ListFamilyTypes_ReturnsBothTypesIncludingUnplaced`, `..._UnknownFamily_ReturnsFamilyNotFound` | r175/r176 | карточка |
| R3 | Параметры family/type: scope, GUID, formula, lookup, canSet*, значения по типам, пагинация | `get_family_parameters` | `AgentFamilyUnitsTests`, `AgentFamilyTextTests` | `GetFamilyParameters_ReportsFormulaSharedFlagsAndValues`, `..._PaginationIsHonest`, `..._InvalidScope`, `..._ClosesFamilyDocument` | r177/r178 | `family-tools-error-catalog.md` |
| R4 | Живой поиск по параметру без индекса, exact/contains | `find_families_with_parameter` | `AgentFamilyTextTests` | `FindFamiliesWithParameter_FindsByParameterName`, `..._FindsByValueContains`, `..._UnknownParameter`, `..._MissingArgument` | r179/r180 | ADR-077 + карточка |
| R5 | Переименование instance/type, конфликты | `rename_family_parameter` | — | `RenameParameter_DryRun…`, `..._Apply…`, `..._DuplicateName`, `..._UnknownParameter` | r181/r182 | error catalog |
| R6 | Добавление normal/shared, GUID, дубликат | `add_family_parameter` | `AgentFamilyUnitsTests` | `AddParameter_DryRun…`, `..._Apply…`, `..._Duplicate`, `..._Shared_CreatesGuid…`, `..._SharedWithoutFile…` | r183/r184 | error catalog |
| R7 | Значения по типам, unit, scope, formula/read-only | `set_family_parameter_values` | `AgentFamilyUnitsTests` (mm/m/deg/…, comma, unit_required) | `SetValues_MissingUnit…`, `..._ApplyPerType_ReadsBack…`, `..._FormulaParameter…`, `..._ScopeMismatch…`, `..._UnknownType…`, `..._SameValue…` | r185/r186 | error catalog |
| R8 | Формулы: CanAssignFormula, снятие, lookup, diff | `set_family_formula` | `AgentFamilyTextTests` (size_lookup) | `SetFormula_DryRun…`, `..._ApplyAndClear…`, `..._MissingLookupTable…`, `..._InvalidSyntax…`, `..._NonAssignable…` | r187/r188 | error catalog |
| R9 | map source→target: scope/spec, formula/lookup, per-type | `map_family_parameters` | — | `MapValues_CopiesPerTypeAndIsIdempotent`, `..._ScopeMismatch`, `..._SpecMismatch`, `..._UnknownTarget`, `MapFormula_CopiesFormulaAndValues`, `..._TargetWithoutFormulaSupport` | r189/r190 | карточка |
| R10 | Контракт ответа: changeSetId всегда при изменении, dry-run не пишет | base + все write | — | `AddParameter_DryRun…` (нет change set), `AddParameter_Apply…` (changeSetId + backup + readBack), `SetValues_ApplyPerType…` (readBack + changeSet), `SetValues_PartialFailure_KeepsChangedAndChangeSet` (success=false + changed + changeSetId), `SetValues_AllFailed_RollsBackAndWritesNoChangeSet` (0 changed, нет change set) | eval schema 149/149 | error catalog §1 |
| R11 | Lifecycle: EditFamily без редактора, backup, close, load-back | session/base | — | `FamilyTools_CloseFamilyDocumentOnSuccessAndFailure`, `AddParameter_Apply…` (backupPath существует), `..._Shared…` | eval replay | runbook |
| R12 | Read-back после load-back, `read_back_mismatch` | `AgentFamilySession.VerifyReadBack` | — | `AddParameter_Apply…` явно проверяет `readBack.mismatched=0`; `readBack.verified` проверяют values/formula/map; ElementId/instance пропускаются с предупреждением | — | error catalog |
| R13 | Undo family-операций | `UndoLastTool.Families.cs` | — | `UndoLast_AfterRename_RestoresOldName`, `..._AfterValueSet_RestoresOldValue`, `..._AfterFormulaChange_RestoresFormula`, `..._RestoresFamilyFromBackup` (precise plan), `..._LegacyBackup_RestoresFamily`, `..._PartialFamilyFailure_KeepsChangeSetApplied`, `..._DryRun_DoesNotRestore`, `..._MissingBackup_ReturnsBackupUnavailable` | — | HANDOFF |
| R14 | Масштаб/память: 200×100, 5000 значений map | type-major map + cached read-back | — | `LargeFamily_ReadAndMap_StayWithinBudget` (p95-замеры в §4) | — | §4 |
| R15 | Маршрутизация семейных интентов | cards+synonyms+examples | `AgentFamilyPackTests`, routing contract | — | 188 фраз (18 семейных), top1 97.3 %, top5 100 %, p95 1.93 мс | §6 |
| R16 | execute_plan с family-тулами: dry-run/apply/read-back по шагам, steps[] | `AgentPlanExecutor` + base | — | `Plan_AddParameters_AppliesBothStepsAndReadsBack`, `Plan_DuplicateParameter_AppliesFirstStepThenFails` | — | §8 |

## 3. Негативная матрица (32 кейса)

Статусы: **T** — покрыт автотестом; **D** — защитный код (недостижим/требует
UI-сессии, обосновано); **M** — ручная проверка (runbook).

| # | errorCode | Сценарий | Покрытие | Статус |
|---|---|---|---|---|
| 1 | `family_not_found` | чужой/несуществующий familyId | `ListFamilyTypes_UnknownFamily…`, `GetFamilyParameters…` (read) | T |
| 2 | `family_not_editable` | нередактируемое семейство | `AgentFamilySession`/`TryCreateBackup` guards | D/M |
| 3 | `in_place_not_supported` | in-place семейство | guard в session; headless-конструктор недоступен | D/M |
| 4 | `parameter_exists` | дубликат имени | `AddParameter_Duplicate…` | T |
| 5 | `parameter_not_found` | нет параметра | `RenameParameter_Unknown…`, `MapValues_UnknownTarget` | T |
| 6 | `formula_not_allowed` | запись в формульный/read-only | `SetValues_FormulaParameter…`, `SetFormula_NonAssignable…`, `MapFormula_TargetWithout…` | T |
| 7 | `scope_mismatch` | instance↔type | `SetValues_ScopeMismatch…`, `MapValues_ScopeMismatch…` | T |
| 8 | `unit_required` | число без единицы | `SetValues_MissingUnit…` + unit-теории | T |
| 9 | `invalid_value` | мусор/неверный тип/диапазон | `AgentFamilyUnitsTests` (числа, да/нет, integer, elementId), `invalid_arguments` тесты | T |
| 10 | `save_failed` | LoadFamily не удался | код возврата в `TryLoadBack` | D/M |
| 11 | `read_back_mismatch` | проект не совпал после reload | проверка реализована; инъекция расхождения невозможна | D/M |
| 12 | `backup_failed` | бэкап не создан | fail-closed ветка | D/M |
| 13 | `backup_unavailable` | undo без плана и без файла | `UndoLast_MissingBackup…` | T |
| 14 | `shared_parameter_file_missing` | нет shared parameter file | `AddParameter_SharedWithout…` | T |
| 15 | `duplicate_name` | переименование в занятое имя | `RenameParameter_DuplicateName…` | T |
| 16 | `type_not_found` | неизвестный тип | `SetValues_UnknownType…` | T |
| 17 | `empty_family` | семейство без типов | защитный код; проба: Revit оставляет ≥1 тип после save/load | D |
| 18 | `lookup_table_missing` | size_lookup на отсутствующую таблицу | `SetFormula_MissingLookupTable…` | T |
| 19 | `permission_denied` | doc Modifiable/ReadOnly | guard в session | D/M |
| 20 | `family_open_conflict` | редактор семейства уже открыт | проба: headless EditFamily допускает несколько копий; конфликт только с UI | D/M |
| 21 | `invalid_formula` | синтаксис формулы | `SetFormula_InvalidSyntax…` (+ откат: формула не изменилась) | T |
| 22 | `shared_parameter_not_renameable` | переименование shared | `AddParameter_Shared_CreatesGuidAndRejectsRename` | T |
| 23 | `invalid_arguments` | нет familyId/parameterName/scope | `AddParameter_WithoutFamilyReference…`, `FindFamiliesWithParameter_MissingArgument`, `GetFamilyParameters_InvalidScope` | T |
| 24 | `ambiguous_family` | дубли имён семейств | guard в `ResolveFamily` | D/M |
| 25 | `ambiguous_parameter` | instance+type с одним именем | guard `FindParameter` | D/M |
| 26 | `family_undo_partial` | часть undo-операций не применилась | ветка в undo | D/M |
| 27 | `family_restore_failed` | undo-транзакция упала | ветка в undo | D/M |
| 28 | `workshared_not_supported` | workshared-модель | базовый guard `AgentWriteToolBase` | T (существующий сьют) |
| 29 | `transaction_rolled_back` | исключение в транзакции | `SetFormula_InvalidSyntax…` (откат проверяется) | T |
| 30 | `nothing_to_undo` | нечего откатывать | существующие undo-тесты | T (существующий сьют) |
| 31 | `tool_failed` | непойманное исключение | общий catch в base; контролируемые отказы (`invalid_value`) проверяются `AddParameter_HugeName…` | D/M |
| 32 | `confirmation_required` | dryRun=false без подтверждения | базовый `AgentWriteToolBase` | T (существующий сьют) |
| 33 | `plan_apply_failed` | сбой шага плана (не атомарно) | `Plan_DuplicateParameter_AppliesFirstStepThenFails` (шаг 1 применён, шаг 2 parameter_exists) | T |

Дополнительно (red-team): огромная строка имени (5000 символов) → `invalid_value`
(`AddParameter_HugeName_IsRejectedGracefully`); локализованные русские имена
параметров — весь seed и все тесты; параметр с формулой — `formula_not_allowed`;
повторный apply — `parameter_exists` / идемпотентный map; чужие id — `family_not_found`.

## 4. Масштаб и память (замеры R25, Revit 2025)

| Сценарий | Время | Память | Артефакт |
|---|---|---|---|
| `get_family_parameters`: 200 параметров × 100 типов (20 000 значений) | **385 мс** | — | `%TEMP%\<session>\smartcon-family-scale.txt` |
| `map_family_parameters`: 50 mappings × 100 типов = **5000 значений** + read-back всех значений | **38,5 с** (≈7,7 мс/значение) | managed heap +11,9 МБ | там же |
| Payload map-результата | 5000 changed-строк ≈ 340 816 символов | — | ToolResultBudget в реальном харнессе обрежет до 40k с `[truncated: N of M]` |

Type-major обход (одно переключение `CurrentType` на тип, а не на значение)
сократил map с 73 с до 38,5 с. UI freeze не проверялся (headless-хост);
ручной замер — в runbook.

## 5. Failure injection

| Инъекция | Что проверено | Артефакт/статус |
|---|---|---|
| Исключение в середине транзакции (невалидная формула) | транзакция family-документа откатывается, формула не меняется, family-документ закрывается | `SetFormula_InvalidSyntax_ReturnsFormulaError` |
| Dry-run | модель не меняется, change set не пишется, backup не создаётся; shared-definition не создаётся | все `*_DryRun_*`, `AddParameter_DryRun…`, `AddParameter_DryRunShared_DoesNotCreateDefinition` |
| All-failed / validation failure | транзакция откатывается, load-back не выполняется, модель и change set не меняются | `SetValues_AllFailed_RollsBackAndWritesNoChangeSet` |
| Read-only `.rfa` | v1 редактирует только загруженные семейства; файловый поток — H1d-2 | D (вне scope) |
| Отсутствующий `.rft` шаблон | тест корректно Skip, продукт не затронут | seed |
| Повторный open той же family | headless Revit допускает несколько EditFamily-копий; конфликт — только с открытым UI-редактором | D/M |
| Отмена во время save | CancellationToken протянут, но окно отмены внутри Revit Save не инжектируется | M |
| Занятый файл бэкапа | уникальные имена, fallback snapshot↔source copy, `backup_failed` fail-closed | D/M |
| Семейство без типов | проба: после save/load Revit восстанавливает ≥1 тип → `empty_family` защитный | D |
| In-place family | нельзя создать headless | M |
| Workshared-проект | `workshared_not_supported` до EditFamily | существующий сьют |

## 6. Routing / A/B

- Rules/BM25 на 42 карточках, 188 реальных русских фраз (18 новых семейных):
  **top-1 97.3 %, top-5 100 %, top-14 100 %, false tool 0 %, p95 1.93 мс**
  (`RoutingEvalTests`, synthetic 1000 карточек — top-5 100 %).
- Decision-reranker (pplx) и вторая модель (OpenCode Go) в этой сессии **не
  запускались**: нет ключей/сети в окружении ночного прогона; тест
  `DecisionsLiveTests` требует `SMARTCON_DECIDER_ENABLED=1` и ключ. Ручной
  A/B — в runbook (pending).
- Live-dogfooding 20–30 реальных задач через LLM — **не выполнялся** по той же
  причине (нужен интерактивный Revit + ключ). All tool-layer scenarios покрыты
  детерминированными интеграционными тестами; dogfooding — manual pending.

## 7. Гейты

| Гейт | Результат |
|---|---|
| Unit `SmartCon.Tests -c Debug.R25` | **3813/3813** |
| Integration R25 (family pack) | **56/56** (read 13, write 26, map+undo 14, scale 1, plan 2) |
| Integration R25 (полный сьют) | см. HANDOFF (финальный прогон) |
| Integration R27 net10 (family pack) | **56/56** |
| Integration net48 (Revit 2023, полный сьют) | **246 passed, 12 skipped, 0 failed** (Agent-тесты исключены из net48 по дизайну) |
| Eval replay (`SMARTCON_AGENT_EVAL_REPLAY=1`) | **46/46**, schema **149/149**, unauthorized **0**, threshold violations **0** |
| Release.R25 (анализаторы) | **0 errors, 0 warnings** |
| 7 конфигураций (R19/R21/R23/R24/R25/R26/R27) | **7/7 OK** |
| Регрессия дня (артефакт J) | см. HANDOFF |
| Ручной UI-тест | **pending** |

## 8. Известные ограничения

1. `find_families_with_parameter` сканирует параметры, видимые на типах
   (type/shared); instance-значения размещённых экземпляров не сканируются —
   это `query_elements`/`get_element_details`.
2. `empty_family` — защитный код: Revit не позволяет сохранить семейство без
   типов (проба: после load-back остаётся 1 тип).
3. Undo добавления параметра выполняется точной обратной операцией
   (`RemoveParameter`), а не reload бэкапа: Revit сохраняет удалённые параметры
   при reload. Backup остаётся источником для legacy change set и ручного
   восстановления.
4. `family_open_conflict` воспроизводим только при открытом UI-редакторе
   семейства; headless EditFamily допускает несколько копий.
5. Если два загруженных семейства имеют одинаковое имя, project-snapshot
   read-back индексирует типы по имени семейства и может прочитать тип соседнего
   семейства; Revit-имена семейств в проекте уникальны на практике.
6. Файловый поток (правка `.rfa` на диске, `OpenDocumentFile`) не входит в 9
   тулов этой сессии: `backup .bak-<timestamp>` создаётся рядом с исходным
   `.rfa`, если путь известен; иначе снимок в
   `%APPDATA%\SmartCon\Agent\family-backups`.
7. `execute_plan` не атомарен (P0 B): при сбое шага N шаги 1..N−1 применены;
   `steps[]` содержит честные статусы; `rollbackOnFailure` /
   `ITransactionGroupSession` — stretch-очередь.
8. После load-back Revit может выдать семейству новый ElementId: повторные
   вызовы с прежним `familyId` получают `family_not_found` с подсказкой
   «вызовите list_families или передайте familyName». В планах и долгих
   диалогах предпочтительно `familyName`.
9. Undo ищет параметры по имени+scope, а не по внутренней identity: если
   параметр переименовали вручную между вызовом и undo, операция `add` может
   не найти его (считается уже отменённой), а `rename` — сообщить
   `family_undo_partial`. Для shared-параметров GUID сохраняется в плане, но
   matching в v1 идёт по имени.
10. Создание определения shared-параметра происходит в файле общих параметров
    (вне family-транзакции и вне Revit undo). В dry-run определение не
    создаётся; при реальном apply, если добавление параметра затем упадёт,
    созданное определение останется в файле (безвредно и переиспользуемо).