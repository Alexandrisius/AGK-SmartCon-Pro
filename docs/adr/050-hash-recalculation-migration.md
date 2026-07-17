# ADR-050: Hash Recalculation Migration — user-initiated data repair с прогресс-диалогом

**Date:** 2026-07-17  
**Status:** accepted  
**Related:** Issue #126, ADR-048 (modeless progress pattern), ADR-049 (hash v2), I-14 (SQLite thread safety), #95/#96 (WPF render workarounds)

## Context

Переход на hash format v2 (ADR-049) делает существующие v1-хэши в пользовательских БД невидимыми для дедупликации (SQL-фильтр `hash_format_version = 2`). Нужна миграция данных: пересчитать хэш каждой версии, открыв её managed-файл в Revit. Это долгая операция (секунды на файл), требующая Revit main thread, отменяемая, с честным прогрессом.

Первоначальный план Issue #126 предлагал: WAL mode, «держать батч из 50 файлов открытыми», пересчёт system families из isolated `.rvt`, триггер внутри `DatabaseManager`, передачу делегата в dialog service. Всё это отклонено (см. комментарий к Issue от 2026-07-17).

## Decision

### 1. Data repair ≠ schema migration

Пересчёт НЕ является частью `LocalCatalogMigrator` (тот остаётся чистым DDL). Отдельный `ICatalogHashRecalculationService` (Core) / `CatalogHashRecalculationService` (FamilyManager, pure C#, юнит-тестируемый с fake-экстрактором). DDL-миграция V22 не нужна: колонки `content_hash`/`hash_format_version` существуют с V16.

### 2. Маркировка состояний в `hash_format_version`

```
NULL / 1 → pending (старая схема)
2        → текущий rename-invariant формат
-1       → RecalculationSkipped: файл безвозвратно нечитаем; навсегда исключён из pending
```

Pending для диалога считается с фильтром `revit_major_version <= текущий Revit`: Revit открывает файлы только своей версии и старше. Версии 2024+ в Revit 2023 молча ждут (диалог их не считает и не донимает), в Revit 2025 появятся сами.

### 3. System — мгновенный UPDATE флага; loadable — пересчёт

System v1-хэши уже rename-invariant (ADR-049 §1) → `UPDATE hash_format_version=2` без открытия файлов. Loadable: группировка по `(catalog_item_id, version_label)`, открывается ОДИН файл на группу (max `revit_major_version` ≤ текущего Revit), хэш применяется ко ВСЕМ Revit-вариантам группы (контент идентичен) — экономия 2–3x открытий.

### 4. Open → extract → close по одному; НЕ держим документы открытыми

В batch import документы держат открытыми ради последующего `SaveAs` (пользователь может отменить/переименовать). Здесь SaveAs нет — удержание десятков документов бессмысленно расходует память. Известная деградация Revit после ~30 циклов open/close (Autodesk forum; лечится только рестартом; наш Warn в `FamilyImportPreparationService` фиксирует то же) принята как данность: миграция разовая, с честным прогрессом и возобновлением. После каждого Close — `RevitBalloonNudge.Nudge` (workaround #96).

### 5. SQLite: существующий DELETE journal + chunked commits

Предложенный WAL отклонён: I-14 требует DELETE journal universally (БД может лежать на SMB-диске, где WAL коррумпируется). Коммиты пачками по 10 файлов в одной транзакции (`busy_timeout=5000` уже настроен). Отмена коммитит текущую пачку → БД консистентна, остаток догоняется при следующем показе диалога.

### 6. Обработка проблемных файлов

- **Файл не найден** → НЕ помечается (может быть временно недоступный диск). Summary: «Не найдено: N» + кнопки **[Удалить записи из каталога]** (явное деструктивное действие пользователя: versions+files+assets, items без версий; если удалённая версия была активной — active переключается на новейшую оставшуюся с ресинком хэша и имени по правилу ADR-049 §3) или **[Оставить]** (остаются pending, напомним позже).
- **Файл не читается** (повреждён/ошибка API) → автоматически `-1` + Warn с `[Action: переимпортируйте семейство]`.
- **Файл не найден на диске** → автоматически `-2` (`RecalculationMissing`) + строка в summary с кнопкой purge. Миграция по определению не может пересчитать отсутствующий файл, поэтому такие записи НЕ держат баннер обновления (баннер — про актуальность хэшей, не про файловую гигиену). Purge недоступных — best-effort: записи БД удаляются всегда; при ошибке доступа к папке (сетевой диск) она сообщается для ручного удаления (`FailedDirectories` в результате).
- **Файл новее текущего Revit** → не трогаем, инфо-строка в summary.

### 7. Триггер — из ViewModel, не из DatabaseManager

`DatabaseManager` — инфраструктурный слой, не знает про UI/Revit API. Проверка `CountPendingAsync` вызывается из `FamilyManagerMainViewModel`: после `InitializeAsync` (после первого ExternalEvent round-trip — иначе `RevitContext` ещё не инициализирован и версия Revit равна 0), на `ActiveDatabaseChanged`, и вручную после `SwitchDatabaseAsync`/`ConnectDatabaseAsync` (там событие подавлено).

**UX (пересмотрено 2026-07-17 после ручного теста): никакого автопоказа диалога.** Пользователь, запустивший Revit не ради плагина, не должен получать модальный диалог при старте. Вместо prompt-а ViewModel молча выставляет `IsDatabaseUpdateRequired`/`PendingDatabaseUpdateCount`: красный badge на кнопке database-tools и команда «Обновить базу данных» в её popup (видна только при pending > 0). Пока база не обновлена, все write-команды модуля (импорт, загрузка в проект, редактирование, удаление, версии, ассеты, категории) блокируются через `EnsureDatabaseUpToDateAsync()` — диалог с объяснением и кнопкой «Обновить сейчас»; просмотр остаётся доступен (база read-only). Блокировка оправдана: v1-хэши не совпадают с v2, поэтому дедуп при stale-базе молча деградирует до name-only.

**Обобщено (2026-07-17):** механизм вынесен в переиспользуемый паттерн — `IDatabaseMigration` + `DatabaseMigrationCoordinator` (см. `docs/architecture/database-migrations.md`). Эта миграция зарегистрирована как `HashRecalculationMigration` (Id=`hash-v2`, Order=10); новые миграции добавляются регистрацией в DI без изменения VM/UI.

### 8. Диалог — паттерн ADR-048 (modeless)

`HashRecalculationProgressViewModel` получает сервис через DI (никаких делегатов в dialog service — отклонено из плана Issue). Показ через `IDialogPresenter.ShowModeless` → на net48 автоматически подключается `BatchDialogRenderRecovery` (workaround #95 — массовые OpenDocumentFile+Close это ровно его триггер). `IProgress<CatalogHashRecalculationProgress>` + «Прервать» (CancellationToken) + summary-экран с purge-кнопкой. X во время работы = запрос отмены (`ICloseAwareViewModel.ConfirmClose`).

## Consequences

**Плюсы:**
- Существующие БД пользователей мигрируются без переустановки/ручной работы; отказ и прерывание безопасны ( chunked commits, re-prompt).
- Нет «вечного диалога»: нечитаемые файлы исключаются навсегда (-1), недоступные — по явному решению пользователя, «новее Revit» — молча ждут.
- Multi-version каталоги корректны: один пересчёт на label, варианты для всех Revit-версий получают хэш бесплатно.
- System families мигрируются мгновенно и без риска рассинхрона.

**Минусы / риски:**
- Разовая длительная операция блокирует Revit (OpenDocumentFile на main thread); смягчено честным прогрессом, прерыванием и возобновлением.
- Деградация Revit после ~30 циклов open/close может замедлить хвост миграции — лечится рестартом Revit (миграция возобновится), обхода не существует.
- До завершения миграции cross-name дедуп работает только для свеже-импортированного (v2) — мягкая деградация, без ложных дублей; загрузка в проект при этом заблокирована (см. §7), поэтому деградация видима пользователю явно.

**Tests:** 11 unit-тестов сервиса (count filters, v2 update + active sync, multi-variant single-open, system reflag, missing/failed/newer-Revit/cancel, purge both modes) на SQLite fixture + fake extractor.
