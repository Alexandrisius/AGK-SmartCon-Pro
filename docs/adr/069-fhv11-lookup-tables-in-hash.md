# ADR-069: FHV11 — lookup tables (таблицы поиска) в едином content hash (#238)

**Date:** 2026-08-23
**Status:** accepted
**Related:** Issue #238, ADR-056 (FHV3), ADR-068 (единый хэш FHV10), ADR-054 (движок актуализации), ADR-058 (min_plugin_version floor)

## Context

Таблицы поиска (`FamilySizeTable` / lookup table CSV) — встроенные в семейство
CSV-таблицы, на которые ссылаются формулы `size_lookup(...)`. Репорт
тестировщиков (#238): при изменении значений в таблице поиска и загрузке новой
версии семейства в FM дедупликация считала файл **дубликатом** — content hash
не менялся. Причина: FHV10 хэшировал текст формулы (секция PARAMS), но не
данные CSV, на которые формула ссылается; `FamilySnapshot` не содержал данных
таблиц, `RevitFamilySnapshotExtractor` их не читал.

Главный архитектурный вопрос — прецедент GROUPS (ADR-068): группы параметров
были ИЗЪЯТЫ из хэша FHV10, потому что ни один merge их не переносит, и
verification-grade разошёлся бы с import-grade. Если бы таблицы поиска не
переносились merge'ем, добавление их в единый хэш зациклило бы stale-update
таких семейств на post-verify mismatch.

## Evidence (зонды в реальном Revit 2025, 2026-08-23)

1. **Читаемость**: содержимое таблицы читается из family-документа как raw CSV
   через `FamilySizeTableManager.ExportSizeTable` — без транзакции, один temp
   файл на таблицу; каноническая строка детерминирована и сдвигается при
   правке значения в CSV.
2. **Merge-transfer: TRANSFERRED в обоих контекстах** — после reload-merge с
   overwrite (`LoadFamilySymbol` в проекте; doc-to-doc `LoadFamily` в
   family-документе) embedded-копия несёт НОВОЕ содержимое таблицы. Значит,
   LOOKUP безопасен в ЕДИНОМ хэше (в отличие от GROUPS): post-verify после
   обновления сходится.
3. **Регрессионные интеграционные тесты** (`LookupTableHashTests`):
   правка CSV сдвигает хэш; embedded hash (EditFamily-копия) == file hash
   после reload с LOOKUP-секцией.

## Decision

1. **`FamilySnapshot` += `LookupTables: IReadOnlyList<LookupTableSnapshot>?`**
   (record: `Name`, `CsvContent` — raw CSV из `ExportSizeTable`,
   нормализованный CRLF→LF + trim trailing whitespace).
2. **`RevitFamilySnapshotExtractor.ExtractFromFamilyDocument`** читает таблицы
   в той же сессии открытия (никаких дополнительных `OpenDocumentFile`):
   `FamilySizeTableManager.GetFamilySizeTableManager(familyDoc, OwnerFamily.Id)`
   → для каждой таблицы `ExportSizeTable` во временный файл → нормализация.
   Источник — именно **raw CSV**, а не `AsValueString`: CSV — машинный формат
   Revit (`##spec##unit` заголовки, сырые значения), локале-инвариантен по
   построению; `AsValueString` — display-форматирование (риск локализации).
3. **`FamilyContentHasher`**: секция `LOOKUP|` в конце loadable canonical
   string (таблицы по `StringComparer.Ordinal` — в семействе может быть
   несколько таблиц; экранирование `|`/`%` как в FHV3+). Префикс
   `FHV11|LOADABLE|`. Секция **опускается целиком** для семейств без таблиц
   (их хэш меняется только префиксом). Системные семейства таблиц не имеют —
   `BuildSystemCanonicalString` не тронут.
4. **`FamilyContentHashFormat.CurrentVersion` = 11**; критическая задача
   актуализации `hash-v11` (Order 11, детект `NOT IN (11, -1, -2)`),
   пересчитывает все строки — механика hash-v10 сохранена (ADR-054).
5. **Floor** `DbCompatibility.CurrentMinPluginVersion` = `2.0.1-beta.9` —
   первая бета, шипящая FHV11 (FHV10 не шипился ни в одном релизе; тег на
   момент решения: v2.0.1-beta.8). Runtime-backfill внутри задачи, монотонно.

## Consequences

- Правка только таблицы поиска теперь сдвигает хэш → новая версия принимается
  (не дубликат) — баг #238 закрыт.
- Существующие базы: критический pending `hash-v11` → баннер + read-only гейт
  до пересчёта (стандартный путь ADR-054). Системные строки перештамповываются
  без изменения хэша (их canonical string не менялся).
- Post-verify stale-update остаётся консистентным (merge-transfer доказан) —
  двухгрейдовая схема НЕ нужна, единый хэш сохраняется.
- Стоимость экстракции: один temp-файл на таблицу на семейство в сессии
  открытия — пренебрежимо (таблицы есть у малой доли семейств, секция
  опускается для остальных).

### Отклонённые альтернативы

| Альтернатива | Почему отклонена |
|---|---|
| `AsValueString` in-memory итерация ячеек | display-форматирование — риск локале-зависимого хэша (RU/EN Revit дали бы разные хэши одной таблицы) |
| Хэш только имён таблиц/числа строк | правка значений ячеек не сдвигала бы хэш — не закрывает баг |
| Отдельный verification-grade без LOOKUP | возврат двухгрейдовой схемы, упразднённой ADR-068; merge-transfer доказал её ненужность |
