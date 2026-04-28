# FamilyManager MVP Scope Matrix

## Цель документа

Scope Matrix фиксирует границы MVP и защищает проект от расползания в enterprise-функции до появления рабочего ядра.

## Главный принцип

MVP должен быть полезным одному пользователю и одновременно архитектурно готовым к корпоративному росту.

Это означает:

- реализуем только local provider;
- проектируем provider abstraction сразу;
- не строим сервер в MVP;
- не смешиваем каталог БД с Revit project storage;
- не делаем governance раньше, чем появится базовый каталог.

## Scope Matrix

| Область | MVP | Post-MVP | Enterprise |
| --- | --- | --- | --- |
| Client module | `SmartCon.FamilyManager` | расширенные команды | несколько рабочих пространств |
| UI | Dockable panel или MVP-window | полноценный workspace | admin dashboards |
| Local catalog | SQLite | sync metadata | offline-first replication |
| File storage | local file cache/source path | content-addressed cache | object storage policies |
| Import | файл и папка `.rfa` | watch folders | server-side ingest |
| Metadata extraction | базовые поля | параметры/типы глубже | validation pipelines |
| Search | name/category/tags/description | FTS5/fuzzy | semantic/AI/OpenSearch |
| Filters | category/status/tags | manufacturer/classification | custom taxonomies |
| Status | Draft/Verified/Deprecated/Archived | In Review/Published | approval workflow |
| Versioning | version record + file hash | diff versions | release channels |
| Load to project | load selected `.rfa` | replace/update | policy-based loading |
| Project usage | local DB history keyed by project identity | server-side usage history | enterprise usage analytics |
| Provider | Local only | Remote read-only | corporate writable provider |
| Auth | нет | token storage abstraction | SSO/OIDC/RBAC |
| Server | нет | reference API prototype | self-hosted multi-tenant |
| Collaboration | нет | shared remote catalog | roles, permissions, audit |
| Analytics | basic counters optional | local usage stats | organization dashboards |
| QA | unit + SQLite tests + Revit smoke | integration smoke | automated validation farm |

## MVP Must Not Include

| Не включать | Почему |
| --- | --- |
| Full server | Увеличит scope и заблокирует локальную ценность |
| SSO/RBAC | Требует security architecture и admin UI |
| Marketplace | Требует юридических условий, moderation, IP model |
| AI search | Нет стабильного корпуса данных и quality baseline |
| OpenSearch | Слишком тяжёлый для MVP |
| EF Core в Revit-клиенте | Лишний вес и риск зависимостей |
| Сторонние WPF themes | Риск конфликтов ResourceDictionary и net48 |
| Автоматическая миграция AppData | Конфликт с текущими storage-инвариантами smartCon |

## MVP Definition of Done

MVP можно считать завершённым, если:

1. FamilyManager открывается из smartCon UI.
2. Пользователь создаёт или выбирает локальный каталог.
3. Пользователь импортирует `.rfa` файл.
4. Пользователь импортирует папку.
5. Каталог сохраняет metadata и hash.
6. Пользователь ищет и фильтрует записи.
7. Пользователь открывает карточку семейства.
8. Пользователь загружает выбранное семейство в активный Revit-проект.
9. Project usage записывается в локальную БД каталога, а не в ExtensibleStorage.
10. Сборки R19/R21/R24/R25/R26 проходят.
11. Existing tests не ломаются.
12. Добавлены тесты для domain, metadata, SQLite и ViewModel.

## Post-MVP Candidates

| Функция | Почему не MVP |
| --- | --- |
| FTS5 | Нужна отдельная smoke-проверка native SQLite bundle |
| Preview generation | Может потребовать дорогого Revit-open workflow |
| Remote read-only provider | Требует API contract и auth decisions |
| Family quality checks | Нужна отдельная модель правил |
| Bulk metadata editing | Можно добавить после стабилизации schema |
| Compare versions | Сначала нужно накопить версионные данные |

## Enterprise Candidates

| Функция | Предусловие |
| --- | --- |
| Corporate server | Provider contract + MVP client |
| Multi-tenant catalog | Security model + org model |
| Approval workflow | Lifecycle model + user roles |
| SSO/OIDC | Credential storage + server identity |
| OpenSearch | Большой объём контента и search telemetry |
| Analytics | Usage events + server-side aggregation |
| Public sharing | Terms/EULA/IP policy |
