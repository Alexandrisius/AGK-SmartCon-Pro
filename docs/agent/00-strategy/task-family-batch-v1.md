# Задача: Family Batch v1 (размер H1c)

> **Статус:** active · 2026-11-19 · владелец: продукт
> **Цель:** закрыть реальный кейс «в тысячах семейств заменить общий параметр» пачками по 20–50, с preview, Save-чекпоинтом и откатом пачки.
> **Не входит:** полный job engine, внешний orchestrator, другие family-операции, поиск.

## 1. Зачем

Сейчас family-тулы работают с одним семейством. На 1000 семейств агент делает 1000 вызовов, раздувает контекст, а при ошибке/закрытии Revit не понимает, что уже изменено и сохранено.

Нужен один пакетный тул, который:
- принимает явный список семейств (пачка 20–50);
- сначала показывает diff «было → станет» по каждому типу;
- применяет только тот же набор (digest);
- после пачки делает `Document.Save()`;
- при ошибке откатывает уже изменённые семейства;
- возвращает отчёт по каждому семейству и `nextCursor`.

Модель вызывает его 20 раз по 50 — это нормальный интерфейс. Никакого цикла в C# по всем 1000 внутри одного вызова.

## 2. Контракт A0-lite (обязательное предусловие)

- Каждый юнит отдаёт `expected`/`actual`/`verified`/`mismatch`; «готово» — только после read-back.
- **Idempotency key:** `familyId + typeName + (shared GUID | BuiltInParameter)`. Перед записью читаем текущее значение: уже равно целевому → `skipped`, без транзакции.
- **Per-item undo пишется до изменения:** старое значение/формула/добавленный параметр; без undo запись запрещена.
- **Document identity + lock:** один активный family-batch на документ; смена документа → `document_changed`, abort.
- **Typed errors:** `family_not_found`, `family_not_editable`, `in_place_not_supported`, `parameter_not_found`, `parameter_exists`, `scope_mismatch`, `formula_not_allowed`, `unit_required`, `invalid_value`, `save_failed`, `backup_failed`, `read_back_mismatch`, `rollback_unavailable`, `document_changed`, `batch_changed`, `preview_expired`.

## 3. Тул `run_family_batch`

### 3.1 Вход

```json
{
  "operation": "map_parameter",
  "familyIds": [123, 124],
  "args": {
    "source": "ТДСК_Наименование",
    "target": "Комментарии",
    "mode": "values"
  },
  "dryRun": true,
  "previewDigest": null,
  "onError": "stop",
  "rollbackOnError": true,
  "savePolicy": "after_batch"
}
```

- `familyIds`: 1–50 за вызов; больше — `too_many_families` (модель делит список).
- `operation`: `map_parameter` (source→target по GUID/имени), `set_parameter_values` (явные значения по типам).
- `dryRun=true` — preview, ничего не сохраняет.
- `dryRun=false` требует `previewDigest`; иначе `preview_required`.
- `savePolicy`: `after_batch` (default) | `never`; `never` → `volatile=true`, предупреждение.
- `onError`: `stop` | `continue`; `rollbackOnError` — откат уже применённых семейств пачки.

### 3.2 Preview

Для каждого семейства и типа:
```json
{"familyId":123,"name":"KR_SHAR_VOLK_15","status":"changed",
 "types":[{"typeName":"Тип 1","parameter":"Комментарии",
           "old":"","new":"Запорный вентиль ВОЛК","verified":false}]}
```
Плюс `counts`, `previewDigest`, `expiresAt` (10 минут). Ничего не пишется; family-документы закрываются через `Close(false)`.

### 3.3 Apply

- проверяет digest + documentId + expiry;
- для каждого семейства: `EditFamily` → правка → read-back в family doc → `LoadFamily`/save по штатной логике `AgentFamilySession`;
- собирает per-item undo/backup;
- после пачки — `Document.Save()` (если `savePolicy=after_batch`); только после успешного Save ставит `saved=true`;
- отчёт: `changed/skipped/failed`, `verified/mismatches`, `backupPath`, `saved`, `nextCursor`.

### 3.4 Ошибки и откат

- `onError=stop` + `rollbackOnError=true`: остановиться на ошибке, откатить уже применённые семейства по undo/backup, вернуть `restored[]`/`restoreFailed[]`.
- `onError=continue`: идти дальше, ошибка в `failed[]` с кодом.
- Если откат невозможен (structural change без backup) — `rollback_unavailable` до начала apply.

### 3.5 Save и рестарт

- До Save изменения живут в памяти: `saved=false` — не прогресс.
- После Save: при рестарте Revit следующий вызов идёт с `nextCursor`/следующим списком; уже применённые семейства пропускаются по read-before-write.
- До Save: пачка переделывается; дублей нет.
- `Document.Save()` не удался (read-only, workshared, отказ) → `save_failed`, `saved=false`, не выдавать «готово».

## 4. Приёмка

- 500 семейств: preview + apply пачками по 20–50, каждый вызов ≤30 с, UI отвечает между вызовами.
- Diff по каждому типу: `old → new`, `verified` после apply.
- Ошибка в 7-м семействе → честный отчёт + откат пачки (или `continue`).
- Kill Revit после Save → resume с cursor, без дублей; до Save → переделка пачки, `false-done=0`.
- Save failure → `save_failed`, `volatile`, без ложного success.
- 20 негативных кейсов из §2 покрыты тестами.
- Dogfood: 10 реальных задач «замени параметр в N семействах»; success ≥90 %, отчёт сверен чтением.
- Unit 100 %, integration R25 (seeded family: shared + formula + instance/type), eval replay 46/46.

## 5. Границы

- Не добавлять job engine/внешний orchestrator.
- Не менять существующие family-тулы без необходимости; batch использует `AgentFamilySession`.
- Не удалять параметры/семейства.
- Не трогать `opencode.json`, `server/`, секреты, docs вне `docs/agent`.
- Не пушить/PR; коммиты локально + HANDOFF.

## 6. Порядок работы

1. A0-lite контракт + тесты (1 день).
2. `run_family_batch` preview (1 день).
3. Apply + Save + undo/rollback (1–2 дня).
4. Интеграционные тесты + dogfood (1 день).
5. Карточка/пак/схема + eval + HANDOFF (0.5 дня).
6. Финальные гейты: unit, R25, net48, R27, 7 конфигураций — один раз.