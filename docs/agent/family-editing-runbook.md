# Runbook: ручная приёмка family editing pack (H1d)

> Все автотесты — детерминированные; этот чек-лист покрывает то, что нельзя
> проверить headless: UI-ощущения, ручной Revit-сценарий, live-модель и
> dogfooding реальным LLM. Отмечать `[x]` по факту.

## 0. Подготовка

- [ ] Ветка `develop`, рабочее дерево содержит коммиты `910fdc7b` и `382786c0`.
- [ ] Сборка и деплой владельцем: `build-and-deploy.bat` (семь конфигураций).
- [ ] В Revit открыт проект (не workshared) с 2–3 загруженными семействами
      (например, окна/двери из шаблона).
- [ ] Лог: `%APPDATA%\AGK\SmartCon\smartcon.log` очищен/ротирован.
- [ ] В настройках SmartCon задан файл общих параметров (Manage → Shared Parameters),
      если будем проверять shared-параметр.

## 1. Read-пакет

- [ ] «Какие семейства загружены?» → `list_families` (или `find_tools` →
      `load_tool_pack families`), ответ: имя/категория/число типов.
- [ ] «Какие типоразмеры у семейства X?» → `list_family_types`, в списке есть
      неразмещённые типы.
- [ ] «Покажи параметры семейства X с формулами» → `get_family_parameters`:
      `scope`, `isShared`, `guid`, `formula`, `valuesByType`, `canSetValue`,
      `canSetFormula`.
- [ ] «Найди семейства с параметром ТДСК_Наименование» →
      `find_families_with_parameter`, в ответе `matchedTypes` и `scopeNote`.

Ожидаемый лог: строки `Agent tool '...'` без `Error`/`Warn`; family-документ
закрывается (в Revit не появляется лишних открытых семейств).

## 2. Write dry-run → apply

- [ ] «Добавь в семейство X текстовый параметр Заказчик» → dry-run:
      `changed=[…]`, `changeSetId` отсутствует, модель не изменилась.
- [ ] Подтвердить → apply: `success=true`, `changeSetId` есть, в `data.backupPath`
      файл существует (`%APPDATA%\SmartCon\Agent\family-backups\…rfa`), в
      `sections` — `open/backup/edit/loadBack/readBack/close` = ok.
- [ ] «Переименуй параметр Марка в МаркаИзделия» → dry-run/apply; проверить
      имя в редакторе семейства.
- [ ] «Задай ширину 1200 мм для типа 1200x2100» → значения в Family Types
      изменились; без unit → `unit_required` и модель не меняется.
- [ ] «Поставь формулу Ширина = Высота * 2» → значения по типам пересчитаны;
      «Сними формулу» → формула исчезла, значение осталось.
- [ ] «Перенеси значения из ТДСК_Наименование в АДСК_Наименование» → отчёт
      per-type; повторный вызов → «No changes» (идемпотентно).
- [ ] «Отмени последнее» / `undo_last` → добавленный параметр исчез; повторный
      `undo_last` → `nothing_to_undo` (или откат предыдущего change set).

## 3. Негативные сценарии (ручные)

- [ ] Открыть семейство в **редакторе** (двойной клик) и попросить агента
      изменить параметр → `family_open_conflict`; семейство не меняется.
- [ ] Попросить изменить in-place семейство (если есть) →
      `in_place_not_supported`.
- [ ] Задать значение формульному параметру → `formula_not_allowed`, модель
      не меняется.
- [ ] Задать значение без единицы → `unit_required`.
- [ ] Переименовать shared-параметр → `shared_parameter_not_renameable`.
- [ ] Убрать файл общих параметров (Manage → Shared Parameters → пусто) и
      добавить shared-параметр → `shared_parameter_file_missing`.
- [ ] Подставить `size_lookup` с несуществующей таблицей →
      `lookup_table_missing`.
- [ ] Удалить файл бэкапа из `family-backups` и вызвать `undo_last` для
      старого (legacy) change set → `backup_unavailable`; для нового — точный
      undo работает без бэкапа.

## 4. Масштаб / память / UI

- [ ] Семейство с 150–200 параметрами и 50–100 типами: `get_family_parameters`
      отвечает < 2 с, UI не замирает.
- [ ] `map_family_parameters` на 50 mapping × 100 типов (≈5000 значений) —
      < 60 с, интерфейс Revit не зависает (операция на API-потоке); в
      `smartcon.log` нет роста памяти/исключений.
- [ ] В ответе больших массивов присутствует `[truncated: N of M …]` и
      `nextOffset` для чтения (`read_tool_result`).

## 5. Регрессия дня (обязательно)

- [ ] Агент на 2-м ходу «продолжай» не теряет ранее загруженные тулы (в запросе
      остаются все loaded/used).
- [ ] `set_view_range` на виде с шаблоном → `view_controlled_by_template`
      (никакого ложного успеха).
- [ ] `apply_view_template` Section → FloorPlan → `template_view_type_mismatch`.
- [ ] `summarize_elements(groupBy=parameter)` с неизвестным параметром →
      `unknown_parameter` + 3 подсказки.
- [ ] `list_schedules.rowCount` = `get_schedule_data.totalRows`.

## 6. Live dogfooding и A/B (pending, нужен ключ)

- [ ] Ключ OpenRouter/OpenCode Go в secrets; `SMARTCON_LIVE=1` (по инструкции
      тестов `RoutingLiveBenchmarkTests`/`DecisionsLiveTests`).
- [ ] 20–30 реальных family-задач на живой модели; транскрипты в `%TEMP%`;
      метрика «успех с первой попытки ≥ 90 %», раундов до правильного вызова,
      `find_tools` на задачу.
- [ ] A/B: 100 family-фраз, rules vs decisions (`pplx`), top-1/top-5/p95;
      вторая модель через OpenCode Go — проверить, что схемы/промпт не завязаны
      на одну модель.
- [ ] Провал → фикс → повторный прогон; результаты — в
      `docs/agent/00-strategy/h1d-evidence.md` §6 и `eval.md`.

## 7. Триаж ошибок

| Симптом | Что смотреть |
|---|---|
| `family_open_failed`/`family_open_conflict` | открыт редактор семейства; закрыть и повторить |
| `save_failed` | в `sections` статус `loadBack=failed`; проверить, что проект не в транзакции, семейство не удалено |
| `read_back_mismatch` | `data.failed[]` с типами/параметрами; откатить `undo_last` или восстановить `backupPath` |
| параметр не появился после undo | новый change set (точный план) vs legacy (reload сохраняет удалённые параметры) |
| бэкап не создан | права на `%APPDATA%\SmartCon\Agent\family-backups`; при известном `.rfa` — рядом с файлом `.bak-<timestamp>` |

Все автоматические гейты и счётчики: `docs/agent/00-strategy/h1d-evidence.md`.