# FamilyManager MVP PRD

## 1. Problem Statement

Проектировщики и BIM-специалисты часто хранят Revit-семейства в папках, сетевых дисках, архивах, старых проектах или личных коллекциях. Такое хранение плохо масштабируется: семейства сложно найти, трудно понять актуальность версии, невозможно быстро отфильтровать по назначению, производителю, категории, типам и параметрам.

Для smartCon FamilyManager должен стать первым модулем, который превращает разрозненные `.rfa` файлы в управляемый BIM-каталог. В MVP фокус не на enterprise-governance, а на базовом, устойчивом и расширяемом ядре: локальная база, импорт, поиск, карточка семейства и загрузка в проект.

## 2. Product Vision

FamilyManager должен развиться в серьёзную платформу управления BIM-контентом:

- локальная библиотека для одиночного пользователя;
- корпоративный серверный каталог;
- управляемые версии;
- статусы качества;
- права доступа;
- self-hosted storage;
- интеграция с проектами Revit;
- аналитика использования;
- в будущем semantic/AI search.

MVP должен заложить фундамент этой платформы, но не пытаться реализовать весь enterprise сразу.

## 3. MVP Goal

MVP цель:

> Пользователь smartCon может создать локальный каталог Revit-семейств, импортировать в него `.rfa` файлы, найти нужное семейство по базовым метаданным и загрузить его в активный Revit-проект.

## 4. Target Users

| Пользователь | Приоритет MVP | Основная потребность |
| --- | --- | --- |
| Инженер / проектировщик | P0 | Быстро найти и загрузить семейство |
| BIM-координатор | P0 | Навести порядок в локальной библиотеке |
| BIM-менеджер | P1 | Подготовить структуру будущей корпоративной библиотеки |
| Корпоративный администратор | P2 | Подключить серверные каталоги и права доступа |
| Разработчик smartCon | P0 | Реализовать модуль без нарушения архитектуры smartCon |

## 5. Platform Scope

| Платформа | Статус | Ожидание |
| --- | --- | --- |
| Revit 2025 / `net8.0-windows` | Primary | Полная MVP-функциональность |
| Revit 2026 / `net8.0-windows` | CI/forward | Сборка и smoke-проверка |
| Revit 2024 / `net48` | Legacy | Компиляция, загрузка, базовая деградация |
| Revit 2021–2023 / `net48` | Legacy | Покрывается конфигурацией R21, ограниченная поддержка |
| Revit 2019–2020 / `net48` | Legacy | Покрывается конфигурацией R19, минимальный режим |

## 6. MVP Functional Scope

### P0 Must Have

| ID | Требование | Acceptance Criteria |
| --- | --- | --- |
| FM-P0-01 | Создать модуль `SmartCon.FamilyManager` | Проект ссылается только на `SmartCon.Core` и `SmartCon.UI`; прямой reference на `SmartCon.Revit` отсутствует |
| FM-P0-02 | Добавить Ribbon entry | В SmartCon ribbon есть панель или кнопка FamilyManager |
| FM-P0-03 | Открывать dockable panel или MVP-окно | Пользователь может открыть рабочую область FamilyManager из Revit |
| FM-P0-04 | Создать локальный каталог | SQLite база создаётся при первом запуске или через Settings |
| FM-P0-05 | Импортировать `.rfa` файл | После импорта запись появляется в каталоге |
| FM-P0-06 | Импортировать папку | Система обрабатывает несколько файлов и показывает отчёт |
| FM-P0-07 | Извлечь базовые metadata | Имя, путь, hash, размер, Revit version, дата, категория при доступности |
| FM-P0-08 | Выполнить поиск | Пользователь ищет по имени, категории, тегам, описанию |
| FM-P0-09 | Показать карточку семейства | Видны metadata, файл, версия, статус, типы при наличии |
| FM-P0-10 | Загрузить семейство в активный проект | `.rfa` загружается в текущий `Document` через Revit layer |
| FM-P0-11 | Логировать ошибки | Ошибки импорта/загрузки пишутся через `SmartConLogger` |
| FM-P0-12 | Сохранить usage history | В локальной БД каталога сохраняется информация о том, какие catalog item/version были загружены в проект |

### P1 Should Have

| ID | Требование | Acceptance Criteria |
| --- | --- | --- |
| FM-P1-01 | Теги и ручное описание | Пользователь может добавить теги и описание |
| FM-P1-02 | Статусы контента | Draft, Verified, Deprecated, Archived доступны в модели |
| FM-P1-03 | Thumbnail/preview cache | Карточка может показывать preview, если оно есть |
| FM-P1-04 | Duplicate detection | Дубликаты определяются по hash |
| FM-P1-05 | Reindex | Пользователь может пересканировать файл или папку |
| FM-P1-06 | Export/Import catalog metadata | Можно перенести metadata между окружениями вручную |

### P2 Could Have

| ID | Требование | Комментарий |
| --- | --- | --- |
| FM-P2-01 | SQLite FTS5 | После проверки native/runtime совместимости |
| FM-P2-02 | Fuzzy search | Можно реализовать простым scoring без ML |
| FM-P2-03 | Пакетное редактирование metadata | Не блокирует MVP |
| FM-P2-04 | Сравнение версий | Полезно, но не требуется для первого релиза |
| FM-P2-05 | Read-only remote provider mock | Может помочь подготовить серверную фазу |

## 7. Non-Goals

MVP не включает:

- полноценный corporate server;
- SSO/OIDC;
- роли и права доступа;
- approval workflow;
- marketplace/public sharing;
- AI semantic search;
- OpenSearch;
- массовое редактирование параметров внутри `.rfa`;
- автоматическую публикацию контента;
- глубокую аналитику использования;
- поддержку всех типов BIM-контента кроме `.rfa`.

## 8. Repository Constraints

MVP должен соблюдать:

- dependency rule: `SmartCon.FamilyManager` не ссылается на `SmartCon.Revit`;
- I-01: Revit API только из main thread / ExternalEvent;
- I-03: проектные транзакции только через `ITransactionService`;
- I-03b: family document transactions разрешены только в Revit layer;
- I-05: не хранить live `Element`, `Family`, `FamilySymbol`;
- I-09: Core pure C#;
- I-10: code-behind пустой;
- I-12: `DataGridColumn.Header` программно;
- I-13 из smartCon не означает хранение каталога семейств в ExtensibleStorage: каталог, metadata, версии, теги, preview и usage history хранятся только в локальной/серверной БД.

## 9. Data Boundary

| Тип данных | Где хранится в MVP |
| --- | --- |
| Каталог семейств | SQLite локальная база |
| `.rfa` файлы | Файловый кэш или исходный путь |
| Preview | Файловый кэш |
| Пользовательские теги | SQLite |
| Статус семейства | SQLite |
| Project usage history | Локальная SQLite БД; в server phase серверная БД |
| UI preferences | Существующий smartCon settings pattern, если нужен |
| Server credentials | Не в MVP |

## 10. Success Metrics

| Метрика | Цель MVP |
| --- | --- |
| Build matrix | R19/R21/R24/R25/R26 без ошибок |
| Existing tests | 0 регрессий относительно текущего baseline |
| New tests | Target: 60+ tests; minimum acceptable: 50 tests вокруг Core/SQLite/ViewModel |
| Import reliability | Ошибка одного файла не останавливает импорт папки |
| Search usability | Пользователь может найти импортированное семейство по имени и тегу |
| Load success | Валидный `.rfa` загружается в активный проект Revit 2025 |
| Upgrade safety | Локальная база не удаляется при обновлении smartCon |

## 11. Open Questions

| Вопрос | Владелец | Блокирует MVP |
| --- | --- | --- |
| Dockable panel с первого MVP или временное модальное окно? | Product/Architecture | Да |
| Копировать `.rfa` в content-addressed cache или хранить ссылку на исходный путь? | Architecture | Да |
| Нужна ли обязательная генерация preview в MVP? | Product | Нет |
| Какой минимальный набор Revit metadata извлекается без дорогого `EditFamily`? | Engineering | Да |
| Какие операции должны быть disabled в legacy Revit? | Architecture | Да |
| Нужна ли локальная база на пользователя или на workspace? | Product | Да |
