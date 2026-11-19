> **ARCHIVED (2026-11-19).** Исторический документ. Не руководство к действию.
> Актуально: [`ROADMAP.md`](../ROADMAP.md) + GitHub Issues (метка `agent`). Внутренние ссылки исторические.

# Family editing pack — каталог ошибок и контракт ответа

> Пакет `families` (H1d), 9 тулов. Все write-тулы: `dryRun=true` по умолчанию,
> запись только после явного подтверждения (`WriteApproved`), модель никогда не
> меняется при `dryRun=true`.

## 1. Единый конверт

```json
{
  "success": true,
  "errorCode": null,
  "message": "1 change(s) applied, 0 skipped, 0 failed.",
  "data": {
    "dryRun": false,
    "changed": [ { "name": "Ширина", "detail": "Тип1", "oldValue": "1000 мм", "newValue": "1100 мм" } ],
    "skipped": [ { "name": "Марка", "detail": "Тип2", "reason": "The type already has this value." } ],
    "failed":  [ { "name": "Площадь", "errorCode": "formula_not_allowed", "reason": "…" } ],
    "sections": [
      { "section": "open",     "status": "ok",      "reason": "family document opened in the background" },
      { "section": "backup",   "status": "ok",      "reason": "backup: …\\family-backups\\SC_…rfa" },
      { "section": "edit",     "status": "ok",      "reason": "set_family_parameter_values: 2 change(s)" },
      { "section": "loadBack", "status": "ok",      "reason": "family loaded back into the project" },
      { "section": "readBack", "status": "ok",      "reason": "2 value(s) verified" },
      { "section": "close",    "status": "ok",      "reason": "family document closed without saving" }
    ],
    "changeSetId": "144ee72b-…",
    "family": "SC_AgentFamilyWrite",
    "backupPath": "C:\\Users\\…\\AppData\\Roaming\\SmartCon\\Agent\\family-backups\\SC_AgentFamilyWrite-20261119-044200.rfa",
    "readBack": { "expected": 2, "verified": 2, "mismatched": 0, "instanceSkipped": 0 }
  }
}
```

Правила:

- `changed[]` — ровно то, что применено (в dry-run — что будет применено).
- `changeSetId` присутствует при любом реальном изменении, **включая
  `success=false`** (частичный сбой). Dry-run change set не создаёт.
- `sections[]` — статусы жизненного цикла: `open`, `backup`, `edit`, `loadBack`,
  `readBack`, `close`; статусы `ok | skipped | failed`.
- `readBack.instanceSkipped` — instance-параметры записаны в тип-дефолты, но в
  проекте без экземпляров не проверяются (честное предупреждение).
- `success=false` при реальной ошибке; чистый no-op → `success=true` и явное
  сообщение «No changes: …» + `skipped[]`.
- All-failed/validation failure откатывает family-транзакцию и не выполняет
  load-back: модель не меняется, change set не пишется.
- `instanceSkipped`/`elementIdSkipped` — значения, которые нельзя проверить в
  проекте без экземпляров / из-за новых ElementId; в ответе есть warning.
- Валидатор обновил политику: `ApplyViewTemplateTool.Fail` теперь выставляет
  `success=false`/`errorCode` и для частичных сбоев (изменённые секции остаются
  в `changed[]`).
- При превышении 40 000 символов `ToolResultBudget` обрезает массивы и
  дописывает в `message` `[truncated: N of M …]`.

## 2. Области действия и единицы

| Понятие | Правило |
|---|---|
| family / type / instance | `list_families` → familyId; `list_family_types` → typeId; параметр имеет `scope=instance|type` |
| `scope` в write | если имя встречается дважды, без `scope` → `ambiguous_parameter`; несовпадение → `scope_mismatch` |
| Числовые значения | обязателен `unit`: length `mm/cm/m/in/ft`, angle `deg/rad`, area `mm2/cm2/m2/ft2`, volume `m3/l/ft3`; без unit → `unit_required`; `number` unit не требует; измеримые spec вне списка (pressure/flow/force) → `invalid_value` (fail-closed) |
| Формулы | только `CanAssignFormula=true`; чтение значения формульного параметра через `valuesByType`; запись значения → `formula_not_allowed` |
| Lookup (`size_lookup`) | перед установкой формулы проверяется наличие таблицы в семействе → `lookup_table_missing` |
| Shared parameters | определение ищется по GUID/имени во ВСЕХ группах файла общих параметров; при отсутствии создаётся в группе SmartCon; GUID сохраняется и возвращается; файл не настроен → `shared_parameter_file_missing`; конфликт имени с другим типом/GUID → `invalid_value` |
| Имена | максимум 255 символов (`invalid_value`), дубликат → `parameter_exists`/`duplicate_name` |

## 3. Коды ошибок

| errorCode | Когда | Сообщение (ru) | [Action] |
|---|---|---|---|
| `family_not_found` | familyId/имя не найдены | «Семейство не найдено в проекте» | `list_families` → актуальный familyId |
| `family_not_editable` | `Family.IsEditable=false` или EditFamily отклонил | «Семейство не редактируется» | выбрать loadable-семейство |
| `in_place_not_supported` | `Family.IsInPlace=true` | «In-place семейство не имеет family-документа» | править вручную в редакторе |
| `family_open_conflict` | семейство открыто в UI-редакторе | «Revit отказался открыть family-документ» | закрыть редактор семейства |
| `family_open_failed` | прочий сбой EditFamily | «Family-документ не открылся» | закрыть редактор, повторить |
| `permission_denied` | проект Modifiable/ReadOnly | «Документ изменяется другой транзакцией» | дождаться/открыть writable-копию |
| `parameter_not_found` | параметра нет | «Параметр не найден» | `get_family_parameters` |
| `parameter_exists` | дубликат имени/GUID | «Параметр уже существует» | переименовать или другое имя |
| `duplicate_name` | переименование в занятое имя | «Другое имя уже занято» | другое имя |
| `ambiguous_parameter` | instance+type с одним именем | «Несколько параметров с таким именем» | передать `scope` |
| `scope_mismatch` | instance↔type | «Параметр экземпляра, запрошен тип» | правильный `scope` |
| `formula_not_allowed` | параметр формульный/read-only | «Параметр вычисляется формулой» | `set_family_formula` или снять формулу |
| `invalid_formula` | синтаксис/циркуляр | «Revit отклонил формулу» | проверить формулу |
| `lookup_table_missing` | нет size_lookup-таблицы | «Lookup-таблица отсутствует» | импортировать CSV |
| `unit_required` | число без единицы | «Требуется единица измерения» | mm/m/deg/… |
| `invalid_value` | мусор/диапазон/тип | «Недопустимое значение» | проверить value/unit/storageType |
| `type_not_found` | typeName/typeId не найден | «Тип не найден» | `list_family_types` |
| `empty_family` | нет типов (защита) | «В семействе нет типов» | создать тип в редакторе |
| `read_back_mismatch` | проект не совпал после reload | «Значения не совпали после загрузки» | откатить: `undo_last` / backup |
| `save_failed` | LoadFamily не удался | «Семейство не загрузилось обратно» | закрыть операции, повторить |
| `backup_failed` | бэкап не создан | «Бэкап не создан, изменение не применено» | проверить доступ к `%APPDATA%\SmartCon\Agent\family-backups` |
| `backup_unavailable` | undo без плана и файла | «Нет плана и бэкапа для отката» | восстановить `.rfa` вручную |
| `shared_parameter_file_missing` | нет shared parameter file | «Файл общих параметров не настроен» | Manage → Shared Parameters |
| `shared_parameter_not_renameable` | переименование shared | «Общий параметр нельзя переименовать в семействе» | править файл общих параметров |
| `builtin_parameter_not_renameable` | встроенный параметр | «Встроенный параметр нельзя переименовать» | — |
| `spec_mismatch` | map между разными spec | «Разные типы данных, конвертация не выполняется» | привести типы |
| `family_undo_partial` | часть undo-операций не применилась | «Откат выполнен частично» | см. `failed[]` |
| `family_restore_failed` | undo-транзакция упала | «Не удалось откатить операции» | backup/undo вручную |
| `invalid_arguments` | нет familyId/parameterName/scope/items | «Не передан обязательный аргумент» | передать аргумент |
| `confirmation_required` | `dryRun=false` без подтверждения | «Требуется подтверждение пользователя» | сначала dry-run |
| `workshared_not_supported` | workshared-модель | «Workshared не поддерживается» | detached-копия |
| `transaction_rolled_back` | исключение в транзакции | «Транзакция откачена, ничего не изменено» | см. message |
| `tool_failed` | непойманное исключение | «Инструмент завершился ошибкой» | лог `smartcon.log` |

## 4. Примеры вызовов

```json
// dry-run добавления параметра
{"familyId": 123456, "name": "Заказчик", "storageType": "text",
 "group": "PG_IDENTITY_DATA", "isInstance": false, "dryRun": true}

// значения по типам (unit обязателен)
{"familyId": 123456, "items": [
  {"parameter": "Ширина", "typeName": "1200x2100", "value": "1200", "unit": "mm"},
  {"parameter": "Марка",  "typeName": "1200x2100", "value": "A-1"}], "dryRun": false}

// формула с lookup
{"familyId": 123456, "parameter": "Размер",
 "formula": "size_lookup(\"PipeSizes\", \"DN\", 0 mm, Ширина)", "dryRun": true}

// map source→target
{"familyId": 123456,
 "mappings": [{"source": {"name": "ТДСК_Наименование"},
               "target": {"name": "АДСК_Наименование"}, "mode": "values"}],
 "dryRun": true}
```

## 5. Локализация

- Сообщения инструментов — английский (как у остальных тулов харнесса), имена
  параметров/групп принимаются и возвращаются как в Revit (`Definition.Name`),
  включая русские.
- Группы: `PG_LENGTH`, `PG_DATA`, `PG_IDENTITY_DATA`, `PG_GENERAL`,
  `PG_GEOMETRY`, `PG_CONSTRAINTS`, `PG_TEXT`, `PG_MATERIALS`, `PG_VISIBILITY`,
  `PG_GRAPHICS`, `PG_CONSTRUCTION` (и русские названия: «Размеры», «Данные»,
  «Идентификация», «Геометрия», «Ограничения», «Текст», «Материалы»,
  «Видимость», «Графика», «Строительство»).
- Единицы принимаются латиницей и кириллицей: `mm/мм`, `m/м`, `deg/град/°`,
  `m2/м²`, `m3/м³`; десятичная запятая (`1,5 м`) поддерживается.