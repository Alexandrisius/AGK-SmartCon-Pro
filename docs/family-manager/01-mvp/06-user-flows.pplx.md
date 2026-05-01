# FamilyManager User Flows

## Цель документа

Документ описывает основные сценарии MVP, edge cases, preconditions и postconditions.

## UC-01: First Run

| Поле | Значение |
| --- | --- |
| Actor | Инженер / BIM-координатор |
| Preconditions | smartCon установлен, Revit открыт |
| Trigger | Пользователь нажимает FamilyManager |
| Main path | Открывается панель, система предлагает создать локальный каталог |
| Postcondition | SQLite база создана, UI показывает empty state |

### Edge Cases

| Ситуация | Поведение |
| --- | --- |
| Нет прав на папку | Показать ошибку и предложить другой путь |
| База повреждена | Создать backup и предложить восстановление |
| Legacy Revit | Показать legacy notice, если часть функций ограничена |

## UC-02: Import Single Family

| Поле | Значение |
| --- | --- |
| Actor | Инженер |
| Preconditions | Local catalog создан |
| Trigger | Import → File |
| Main path | Выбор `.rfa` → hash → metadata extraction → запись в SQLite → refresh grid |
| Postcondition | Семейство видно в каталоге |

### Edge Cases

| Ситуация | Поведение |
| --- | --- |
| Файл не `.rfa` | Пропустить с сообщением |
| Файл недоступен | Ошибка без падения UI |
| Дубликат hash | Показать duplicate state |
| Не удалось извлечь metadata | Создать запись с minimal metadata |

## UC-03: Import Folder

| Поле | Значение |
| --- | --- |
| Actor | BIM-координатор |
| Preconditions | Local catalog создан |
| Trigger | Import → Folder |
| Main path | Скан папки → список файлов → batch import → progress → summary |
| Postcondition | Валидные файлы добавлены, ошибки показаны в отчёте |

### Acceptance

- Ошибка одного файла не останавливает весь batch.
- Пользователь видит progress и может отменить операцию.
- Логи пишутся через `SmartConLogger`.

## UC-04: Search and Filter

| Поле | Значение |
| --- | --- |
| Actor | Инженер |
| Preconditions | В каталоге есть записи |
| Trigger | Ввод в search box или выбор фильтра |
| Main path | UI обновляет список по name/category/tags/status |
| Postcondition | Пользователь видит релевантные записи |

### MVP Search Fields

- name;
- description;
- category;
- tags;
- manufacturer;
- status.

## UC-05: View Family Details

| Поле | Значение |
| --- | --- |
| Actor | Инженер |
| Preconditions | Запись выбрана |
| Trigger | Selection changed |
| Main path | Правая карточка показывает metadata, file, version, types, preview |
| Postcondition | Пользователь понимает пригодность семейства |

### Empty States

| State | UI |
| --- | --- |
| Нет preview | Preview unavailable state |
| Нет типов | "Types were not indexed" |
| Файл missing | Warning |
| Deprecated | Warning badge |

## UC-06: Load Family to Active Project

| Поле | Значение |
| --- | --- |
| Actor | Инженер |
| Preconditions | Активен Revit document, выбран catalog item |
| Trigger | Load to Project |
| Main path | Resolve file → Revit layer loads family → project usage saved |
| Postcondition | Семейство доступно в проекте |

### Revit Rules

- ViewModel не вызывает Revit API напрямую.
- Операция идёт через Core interface и `SmartCon.Revit` implementation.
- Project transaction используется через `ITransactionService`, если требуется.
- Не хранить `Family`/`FamilySymbol` в ViewModel.

### Edge Cases

| Ситуация | Поведение |
| --- | --- |
| Нет активного документа | Disable command |
| Файл missing | Показать action: Locate file |
| Семейство уже загружено | Показать Already loaded / Reload option |
| Ошибка Revit API | Лог + понятное сообщение |

## UC-07: Edit Metadata

| Поле | Значение |
| --- | --- |
| Actor | BIM-координатор |
| Preconditions | Запись существует |
| Trigger | Edit metadata |
| Main path | Изменить tags/description/status → save → SQLite update |
| Postcondition | Search index обновлён |

## UC-08: Reindex

| Поле | Значение |
| --- | --- |
| Actor | BIM-координатор |
| Preconditions | File exists |
| Trigger | Reindex |
| Main path | Повторно вычислить hash/metadata → обновить version или item |
| Postcondition | Каталог актуализирован |

## UC-09: Delete from Catalog

| Поле | Значение |
| --- | --- |
| Actor | BIM-координатор |
| Preconditions | Запись выбрана |
| Trigger | Delete |
| Main path | Confirm → remove catalog item or mark archived |
| Postcondition | Запись скрыта или удалена согласно policy |

### MVP Decision

По умолчанию MVP должен предпочитать `Archived`, а не физическое удаление файла.

## UC-10: Legacy Mode

| Поле | Значение |
| --- | --- |
| Actor | Любой |
| Preconditions | Revit 2019–2024 |
| Trigger | Открытие FamilyManager |
| Main path | UI открыт, функции с unsupported API отключены |
| Postcondition | Плагин не падает |

## Flow-Level Logging

Каждый flow должен логировать:

- start;
- success;
- failure;
- file count для batch;
- elapsed time для import/load;
- exception type/message без sensitive token/path leakage.
