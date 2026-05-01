# FamilyManager для smartCon: концепция ТЗ и roadmap

## 1. Резюме

FamilyManager стоит проектировать не как «браузер папки с RFA», а как будущую платформу управления BIM-контентом внутри smartCon: локальная библиотека для одиночных пользователей, подключаемые корпоративные каталоги, серверные хранилища, контроль качества, версии, роли, аналитика и интеграция с рабочими сценариями Revit. Ваша стартовая гипотеза правильная: сначала дать любому пользователю простой путь «скачал smartCon → добавил свои семейства → нашёл → загрузил в проект», а затем поверх этого наращивать корпоративный слой.

Главный продуктовый риск: слишком рано уйти в «огромную enterprise-платформу» и не довести базовый сценарий до ежедневной полезности. Поэтому roadmap должен идти от локального каталога и удобного dockable panel к серверным провайдерам, governance, заявкам, аналитике и marketplace-подобной модели.

Архитектурно FamilyManager должен быть отдельным модулем smartCon, построенным по существующим инвариантам проекта: Core без вызовов Revit API, Revit API только через `SmartCon.Revit`, WPF/MVVM через CommunityToolkit, Revit-вызовы из dockable panel только через ExternalEvent, транзакции только через `ITransactionService`, а весь каталог BIM-контента хранится вне `.rvt` в локальной или серверной БД. ExtensibleStorage не является хранилищем FamilyManager и не используется для каталога, версий, metadata, тегов, preview, поиска, истории загрузок или избранного.

## 2. Исходная продуктовая идея

Целевой модуль: WPF dockable panel внутри Revit, который позволяет управлять семействами как структурированным BIM-контентом, а не как файлами в папках.

Базовая идея:

- пользователь скачивает smartCon;
- создаёт или подключает локальную базу семейств;
- импортирует `.rfa`, папки, возможно `.rvt`-источники с системными семействами;
- получает поиск, фильтры, карточки, версии, теги, превью и загрузку в активный проект;
- корпоративный клиент подключает серверную БД или self-hosted каталог;
- в будущем появляется полноценное управление BIM-контентом: статусы, согласование, качество, заявки, права, аудит, аналитика, связи с параметрами и номенклатурой.

HTML-набросок уже показывает направление «Industrial Edition»: фильтры по производителю, статусам, BIM-версии, техническим параметрам DN/PN/материал, режимы поиска Standard / AI semantic / Parametric, действия сравнения, экспорта и загрузки в проект, карточка семейства с версиями, типоразмерами, метаданными, документацией, поставщиками и историей изменений. Это стоит рассматривать как дальний UX-ориентир, но MVP должен быть гораздо уже.

## 3. Позиционирование

FamilyManager в smartCon должен занять нишу между простыми Revit family browser-инструментами и тяжёлыми корпоративными BIM content management платформами.

Формулировка продукта:

> FamilyManager is a Revit-native BIM content manager for smartCon that helps individual users and BIM teams build, search, validate, load, version and govern Revit families through local and server-backed catalogs.

На русском:

> FamilyManager это встроенный в Revit менеджер BIM-контента для smartCon, который позволяет частным пользователям и BIM-командам создавать локальные и серверные каталоги семейств, быстро искать и загружать контент, управлять версиями, метаданными, статусами и качеством.

Ключевое отличие от простого браузера:

- FamilyManager не просто открывает `.rfa` из папки;
- он индексирует метаданные и параметры;
- понимает версии, статусы и источники;
- даёт единый интерфейс для локальной и серверной БД;
- хранит историю использования и связи с проектом в локальной или серверной БД;
- в будущем становится ядром BIM content governance.

## 4. Рыночные ориентиры

Рынок уже подтвердил ценность такой категории. Autodesk позиционирует Content Catalog как cloud-based digital asset management и single source of truth для хранения, версионирования, поиска и доступа к BIM-контенту, включая Revit families, с прямой интеграцией в Revit и вставкой поддерживаемого контента в модель ([Autodesk Content Catalog](https://www.autodesk.com/products/autodesk-docs/features/content-catalog)). Это означает, что управление BIM-контентом больше не является нишевой надстройкой, а становится частью базовой AEC data management стратегии.

ПИК описывает Family Manager как ядро платформы проектирования, которое выросло из библиотеки семейств в систему с ролями, статусами, версионированием, функциональными типами, требованиями к параметрам, проверками качества, заявками, связями с закупочной номенклатурой, пакетной обработкой и историей изменений ([статья ПИК на Habr](https://habr.com/ru/companies/pik_digital/articles/886618/)). Это важный ориентир для «большого» видения, но не для первой фазы smartCon.

UNIFI Pro рекомендует управлять библиотеками через понятные структуры: global libraries, sandbox libraries, discipline libraries, project libraries, security group libraries, теги и saved searches, причём акцент делает на минимальном количестве библиотек и понятной governance-модели ([UNIFI Pro Library Management Best Practices](https://help.unifipro.autodesk.com/en/articles/9491896-library-management-best-practices)). Для smartCon это хороший принцип: не плодить сложность в UI, а дать гибкую модель библиотека плюс теги плюс сохранённые поиски.

BIM&CO Onfly делает акцент на поиске по категориям, классификациям, тегам и параметрам, workflow review/publishing, property governance, spaces/permissions, обновлениях объектов и кросс-платформенных плагинах для Revit, AutoCAD, BricsCAD и других инструментов ([BIM&CO Onfly](https://www.bimandco.com/bim/bim-library-content-management/)). Для smartCon это ориентир enterprise-фаз, где важнее правила, права и единый язык параметров.

ARKANCE Smart Browser обещает быстрое управление Revit libraries, batch editing, работу с параметрами без ручного открытия каждого семейства, стандартизацию библиотеки и проверку контента проекта на соответствие библиотеке ([ARKANCE Smart Browser](https://arkance.world/us-en/products/be-smart/building/smart-browser)). Для smartCon это ориентир на BIM-manager инструменты и массовые операции.

Kinship позиционирует себя как Revit content management tool с shared/restricted/project-specific libraries, Revit add-in, smart filters, rich previews, project tracking, analytics, SSO, SQL/API access в Enterprise-тарифе ([Kinship](https://kinship.io)). Для smartCon это ориентир на аналитику использования и корпоративную открытость данных.

AVAIL делает сильный акцент на file-agnostic indexing: контент можно индексировать из разных мест без перемещения файлов, включая OneDrive, BIM360, Egnyte, а также работать с RVT, PNG, PDF и другими типами ([AVAIL](https://getavail.com)). Для smartCon это сигнал: не обязательно заставлять клиента сразу переносить весь контент в новую БД, можно начать с индексации существующих источников.

KiwiCodes Family Browser показывает зрелый «практичный» слой: локальное, LAN или cloud-based расположение контента, drag-and-drop загрузка, превью, теги, поиск по имени семейства, типу, категории, значению параметра, поддержка system families, schedules и drafting views, аналитика и роли Admin/User ([Autodesk App Store: Family Browser R4](https://apps.autodesk.com/RVT/en/Detail/Index?id=1455574905646266787&appLang=en&os=Win64)). Для smartCon это ближайший ориентир MVP/первой коммерческой версии.

MEPcontent и BIMsmith показывают другую категорию: публичные каталоги производителей. MEPcontent Browser позволяет искать manufacturer-specific и generic BIM content, получать актуальную графическую и параметрическую информацию и загружать или вставлять 3D-контент напрямую в Revit или CAD без выхода из проектной среды ([MEPcontent](https://www.mepcontent.com/en/bim-files/)). BIMsmith даёт бесплатную cloud-платформу для исследования, конфигурации и скачивания строительных продуктов и Revit-данных производителей ([BIMsmith](https://bimsmith.com)). Для smartCon это направление будущего marketplace/content hub, но не обязательная часть первых фаз.

## 5. Конкурентная карта

| Продукт | Сильная идея | Что взять в FamilyManager |
| --- | --- | --- |
| Autodesk Content Catalog | Single source of truth, облачное хранение, версии, Revit add-in, доступ через Autodesk ecosystem ([Autodesk Content Catalog](https://www.autodesk.com/products/autodesk-docs/features/content-catalog)) | Серверный провайдер, версии, прямой insert/load из Revit, совместимость с проектом |
| ПИК Family Manager | Функциональные типы, проверки параметров, заявки, привязки к закупкам, история изменений ([Habr](https://habr.com/ru/companies/pik_digital/articles/886618/)) | Долгосрочное видение: content governance плюс автоматизация проектирования |
| UNIFI Pro | Библиотеки, sandbox workflow, permissions, saved searches, структура по дисциплинам/проектам ([UNIFI Pro](https://help.unifipro.autodesk.com/en/articles/9491896-library-management-best-practices)) | Простая библиотечная модель, sandbox, сохранённые поиски |
| BIM&CO Onfly | Classification, property governance, review/publishing, spaces/permissions ([BIM&CO Onfly](https://www.bimandco.com/bim/bim-library-content-management/)) | Enterprise governance, правила параметров, spaces |
| Smart Browser | Batch editing, управление параметрами без ручного открытия, контроль актуальности ([ARKANCE Smart Browser](https://arkance.world/us-en/products/be-smart/building/smart-browser)) | BIM-manager инструменты, массовые операции, quality check |
| Kinship | Project tracking, analytics, SSO, SQL/API access ([Kinship](https://kinship.io)) | Аналитика использования, corporate admin, API |
| AVAIL | Индексация контента без перемещения, разные источники и типы файлов ([AVAIL](https://getavail.com)) | Индексировать существующие папки и облака, а не требовать миграцию с первого дня |
| KiwiCodes Family Browser | Практичный browser: drag-and-drop, preview, tags, parameter search, system families ([Autodesk App Store](https://apps.autodesk.com/RVT/en/Detail/Index?id=1455574905646266787&appLang=en&os=Win64)) | MVP-UX для одиночных и малых команд |
| MEPcontent / BIMsmith | Публичные каталоги производителей и product data ([MEPcontent](https://www.mepcontent.com/en/bim-files/), [BIMsmith](https://bimsmith.com)) | Будущий публичный каталог, производители, спецификации, product data |

## 6. Архитектурная рамка smartCon

FamilyManager должен быть встроен как отдельный модуль, а не как набор разрозненных команд.

Рекомендуемая структура:

- `SmartCon.FamilyManager`: WPF/MVVM модуль, команды, ViewModels, Views, сервисы UI-слоя;
- `SmartCon.Core`: доменные модели и интерфейсы, не вызывающие Revit API;
- `SmartCon.Revit/FamilyManager`: реализации Revit API, загрузка семейств, извлечение метаданных, preview, анализ активного проекта;
- `SmartCon.FamilyManager/Services/LocalCatalog`: локальная SQLite БД, миграции, repository layer, кэш превью, индекс, настройки пользователя;
- отдельный сервер или reference server в будущем: HTTP API, storage, auth, roles, organization scopes.

Ключевые инварианты:

- Revit API нельзя вызывать из WPF напрямую;
- dockable panel работает через ExternalEvent;
- транзакции только через `ITransactionService`;
- нельзя хранить живые `Element`, `FamilySymbol`, `Connector` между транзакциями;
- Core не должен зависеть от WPF и не должен вызывать Revit API;
- локальный или серверный каталог нельзя хранить в `.rvt`;
- project usage, журнал загрузок, избранное и связи с источником каталога хранятся в локальной или серверной БД, а не в `.rvt`;
- БД должна переживать обновление smartCon;
- клиентские зависимости, попадающие в legacy-сборки, должны поддерживать `net48` или быть изолированы; серверные зависимости не обязаны поддерживать `net48`, потому что сервер не загружается в Revit.

Dockable panel это отдельная инфраструктурная задача: в текущей архитектуре smartCon ещё нет `IDockablePaneProvider`, поэтому первая техническая фаза должна создать правильный lifecycle панели, регистрацию в `App.OnStartup`, singleton content/VM и безопасную реакцию на смену активного документа.

## 7. Доменная модель

На концептуальном уровне домен можно описать так:

| Сущность | Назначение |
| --- | --- |
| Catalog | Источник контента: Local, Remote, Corporate, Public |
| Library / Space | Логическая зона внутри каталога: личная, проектная, дисциплинарная, корпоративная |
| FamilyCatalogItem | Единица контента: `.rfa`, системное семейство, schedule, drafting view в будущих фазах |
| FamilyCatalogVersion | Версия файла, Revit version, BIM standard version, hash, changelog |
| FamilyTypeDescriptor | Типоразмеры и параметры типов: DN, PN, размеры, материал, Kvs и т.д. |
| Metadata | Производитель, категория, OmniClass, ключевые параметры, теги, описание |
| Preview | Иконка, 2D/3D preview, скриншот, generated thumbnail |
| DocumentLink | Документация: паспорта, сертификаты, PDF, DWG, инструкции |
| Status | Draft, In Review, Verified, Deprecated |
| Visibility | Private, Organization, Public |
| UsageRecord | Кто, когда, куда загрузил или обновил семейство |
| QualityRule | Требования к параметрам, именованию, версии, категории |
| Provider | Абстракция источника: SQLite, HTTP, корпоративный сервер, будущий marketplace |

Важно заложить `Visibility = Private | Organization | Public` с самого начала, даже если MVP работает только в `Private`. Это позволит потом не переписывать модель при появлении серверных БД и публичного каталога.

## 8. Принципы продукта

1. Сначала daily-use, потом enterprise.
2. Каталог должен работать без сервера.
3. Сервер должен быть провайдером, а не отдельным продуктом, ломающим UX.
4. Поиск и загрузка должны быть быстрее, чем Windows Explorer плюс Load Family.
5. База хранит метаданные и индекс, но файлы могут жить локально, в кэше, на сервере или в корпоративном storage.
6. Пользователь должен понимать источник семейства: local, organization, public, project.
7. Статус Verified должен означать проверенный контент, а не просто красивый badge.
8. BIM-manager сценарии не должны мешать обычному пользователю.
9. Любое Revit API действие должно быть безопасно вынесено в ExternalEvent.
10. Каждая большая архитектурная развилка должна получить ADR.

## 9. Персоны

### Individual Revit user

Хочет быстро добавить свои семейства в локальную библиотеку, найти их по имени/категории/параметрам и загрузить в проект без ручного поиска по папкам.

### BIM manager

Хочет централизовать семейства, контролировать качество, статусы, версии, теги, правила параметров и видеть, что реально используется в проектах.

### Corporate admin

Хочет подключить серверную БД, управлять доступами, ролями, пространствами, правами видимости, безопасностью и обновлениями.

### Content author

Создаёт и обновляет семейства, добавляет метаданные, документацию, типоразмеры, changelog, отправляет на проверку.

### Designer / engineer

Не хочет администрировать библиотеку. Ему нужен поиск, фильтры, превью, понятный статус и кнопка «Загрузить в проект».

### Manufacturer / public publisher

В дальнейшей перспективе публикует product data, документацию и BIM-контент для пользователей smartCon.

## 10. Roadmap

Roadmap ниже намеренно концептуальный. Он не заменяет технический план по каждой фазе, а задаёт последовательность развития продукта.

### Phase 0. Foundation: архитектурное решение и UX-прототип

Цель: зафиксировать границы модуля, lifecycle dockable panel и модель каталогов до написания большого объёма кода.

Содержание:

- ADR по dockable panel lifecycle;
- ADR по catalog provider abstraction;
- решение, где лежит локальная БД и кэш;
- первичная доменная модель: FamilyCatalogItem, FamilyCatalogVersion, Metadata, Provider, FamilyCatalogQuery;
- UX-каркас panel: дерево/список/карточка/поиск;
- критерии MVP: импорт, поиск, карточка, загрузка в проект.

Результат: smartCon понимает, что FamilyManager это отдельный модуль, а не разовая команда.

### Phase 1. MVP Local Catalog

Цель: дать одиночному пользователю полезный локальный менеджер семейств.

Содержание:

- локальный каталог в `%APPDATA%\AGK\SmartCon\FamilyManager`;
- SQLite через `Microsoft.Data.Sqlite 8.x`;
- импорт `.rfa` файла и папки;
- базовая индексация: имя, путь, категория, дата, hash, версия Revit, пользовательские теги;
- кэш превью;
- поиск по имени, категории, тегам;
- карточка семейства;
- загрузка выбранного семейства в активный проект через ExternalEvent;
- базовый журнал использования.

Не включать:

- сервер;
- роли;
- согласование;
- AI-поиск;
- marketplace;
- сложные правила качества.

Критерий успеха: пользователь перестаёт пользоваться Windows Explorer для типовых семейств и открывает FamilyManager каждый день.

### Phase 2. Rich Metadata and Parametric Search

Цель: превратить библиотеку из списка файлов в BIM-каталог.

Содержание:

- извлечение параметров семейств;
- типоразмеры и FamilyTypeDescriptor;
- фильтры по параметрам;
- технические атрибуты: DN, PN, материал, размеры, производитель;
- сохранённые поиски;
- ручное редактирование метаданных;
- импорт/экспорт метаданных в JSON/CSV/Excel-like формат;
- статусы Draft / Verified / Deprecated как локальные метки;
- сравнение версий на уровне метаданных.

Результат: пользователь ищет не «файл крана», а «шаровой кран DN50 PN16 verified».

### Phase 3. Revit Project Integration

Цель: связать каталог с активным проектом, а не просто загружать файлы.

Содержание:

- проверка, загружено ли семейство в проект;
- Load / Place / Replace / Update сценарии;
- project usage history, избранное и связи с источником каталога в локальной/серверной БД с идентификатором проекта;
- предупреждение о deprecated или outdated family;
- связь с существующими smartCon сценариями, особенно PipeConnect;
- отчёт «какие семейства проекта не из каталога»;
- отчёт «какие семейства проекта устарели».

Результат: FamilyManager становится инструментом контроля проекта, а не только библиотекой.

### Phase 4. Team Catalog and Shared Storage

Цель: дать малым командам общий каталог без полноценного enterprise-сервера.

Содержание:

- shared folder / LAN / cloud-synced folder mode;
- блокировки или optimistic concurrency;
- роли на уровне простой модели: Owner, Editor, Viewer;
- sandbox library для непроверенного контента;
- перенос из sandbox в verified library;
- правила именования и обязательные поля;
- базовая аналитика использования.

Результат: небольшая проектная команда может вести общую библиотеку без разворачивания серверного продукта.

### Phase 5. Remote Provider and Corporate Server

Цель: подключить корпоративных клиентов к серверной БД и сделать FamilyManager multi-provider.

Содержание:

- `IFamilyCatalogProvider` с реализациями Local и HTTP Remote;
- конфигурация server endpoint;
- авторизация: PAT/JWT/OAuth2-подобная схема;
- безопасное хранение credentials через Windows Credential Manager;
- organization scope;
- server-side search;
- download-on-demand и локальный cache;
- upload с metadata и preview;
- reference self-hosted server;
- документация по deployment.

Результат: один UI работает и с личной локальной базой, и с корпоративным каталогом.

### Phase 6. Governance and Quality

Цель: перейти от хранения к управлению качеством BIM-контента.

Содержание:

- workflow Draft → In Review → Verified → Deprecated;
- роли: Author, Reviewer, BIM Manager, Admin;
- правила обязательных параметров;
- property governance: единые имена, типы, допустимые значения;
- автоматические проверки семейства при загрузке в каталог;
- отчёты об ошибках;
- история изменений;
- заявки на создание или исправление семейства;
- вложения: PDF, DWG, XLSX, сертификаты, инструкции.

Результат: FamilyManager становится системой управления стандартом BIM-контента.

### Phase 7. Enterprise Content Platform

Цель: зрелая платформа для крупных организаций.

Содержание:

- multi-tenant organization model;
- SSO;
- API/SQL-export или BI-friendly доступ;
- аудит действий;
- usage analytics;
- project dashboards;
- массовые операции;
- batch upgrade Revit versions;
- правила жизненного цикла;
- интеграции с CDE, DMS, закупками, номенклатурой;
- шаблоны проектов, schedules, drafting views, materials, system families;
- optional public/private marketplace.

Результат: FamilyManager становится серьёзным BIM content management предложением, сопоставимым с enterprise-категорией.

### Phase 8. AI and Semantic Layer

Цель: добавить интеллектуальный поиск и помощь, когда база уже достаточно структурирована.

Содержание:

- semantic search по описанию задачи;
- нормализация параметров;
- подсказки тегов;
- поиск аналогов;
- рекомендации замены deprecated families;
- поиск по естественному языку: «задвижка для питьевой воды DN150»;
- quality assistant для проверки metadata completeness;
- генерация changelog/описания.

Важно: AI не должен быть частью MVP. Он полезен только после накопления качественных метаданных.

## 11. MVP: рекомендуемая граница

MVP должен быть маленьким, но законченным.

Включить:

- dockable panel;
- локальная БД;
- импорт `.rfa`;
- импорт папки;
- список/плитки/дерево;
- поиск;
- теги;
- карточка семейства;
- preview;
- загрузка в проект;
- журнал использования;
- базовая локализация RU/EN;
- unit-тесты Core-сериализации, фильтрации и моделей.

Не включать:

- сервер;
- корпоративные роли;
- workflow согласования;
- AI;
- заявки;
- публичный каталог;
- интеграции с закупками;
- batch edit параметров;
- автоматический upgrade Revit versions.

Почему так: если MVP сразу попытается быть Onfly, UNIFI, AVAIL и ПИК Family Manager одновременно, он станет слишком большим. Первая версия должна доказать, что smartCon может быть ежедневным инструментом поиска и загрузки семейств.

## 12. Серверная стратегия

Серверный режим лучше проектировать не как «замена локальной БД», а как ещё один provider.

Абстракция:

```text
IFamilyCatalogProvider
  LocalCatalogProvider
  SharedFolderCatalogProvider
  RemoteCatalogProvider
  PublicReadOnlyProvider
```

UI не должен знать, где лежит контент. Он должен выполнять `Search`, `GetDetails`, `Download`, `Upload`, `UpdateMetadata`, `GetVersions`.

Сервер должен отвечать за:

- хранение метаданных;
- хранение или ссылки на `.rfa`;
- права доступа;
- workflow;
- индексацию;
- аудит;
- версии;
- организации и пространства.

Клиент smartCon должен отвечать за:

- UI;
- локальный кэш;
- Revit API операции;
- загрузку в проект;
- извлечение metadata из Revit;
- offline-friendly сценарии.

## 13. Данные и хранение

Рекомендуемая стратегия:

- локальная БД: `%APPDATA%\AGK\SmartCon\FamilyManager\familymanager.db`;
- кэш файлов: `%APPDATA%\AGK\SmartCon\FamilyManager\cache`;
- кэш превью: `%APPDATA%\AGK\SmartCon\FamilyManager\cache\previews`;
- настройки пользователя: `%APPDATA%\AGK\SmartCon\FamilyManager\settings.json`, если они не относятся к конкретному Revit-проекту;
- project usage, избранное и история загрузок: локальная БД в MVP, серверная БД в corporate phase;
- серверные credentials: Windows Credential Manager;
- server endpoint: per-user setting; корпоративные project policies появятся только в server phase и должны храниться в серверной БД.

Не хранить полный каталог в `.rvt`. Это раздует модель, создаст проблемы Worksharing и нарушит смысл глобального каталога.

## 14. UX-направление

Основной layout:

- левая зона: каталоги, библиотеки, категории, сохранённые поиски;
- верх: search bar и режимы поиска;
- центр: список/плитки семейств;
- правая зона или нижняя панель: карточка семейства;
- footer actions: Load, Place, Compare, Export, Add to favorites;
- отдельные модальные окна: Import Wizard, Metadata Editor, Server Connection, Quality Report.

Фильтры:

- категория Revit;
- производитель;
- статус;
- версия Revit;
- BIM standard version;
- теги;
- технические параметры;
- источник: local / organization / public;
- loaded in current project;
- outdated / deprecated.

Карточка семейства:

- preview;
- описание;
- статус;
- версия;
- Revit version;
- производитель;
- параметры;
- типоразмеры;
- документы;
- история изменений;
- usage;
- кнопки Load / Place / Update / Open source file.

## 15. Риски

### Scope creep

Самый большой риск: попытаться сразу построить enterprise-платформу. Нужно строго отделить MVP Local Catalog от серверного и governance слоя.

### Revit API lifecycle

Dockable panel не даёт права напрямую вызывать Revit API. Все операции загрузки, анализа активного документа, замены семейств и транзакции должны идти через ExternalEvent.

### Производительность

Массовое открытие `.rfa` через Revit API дорого. Нужна двухфазная индексация: сначала дешёвые данные из файла/пути/кэша, затем глубокий анализ по требованию или фоновыми задачами, где это безопасно.

### Совместимость net48

Любая новая клиентская библиотека, попадающая в Revit add-in и legacy-сборки, должна поддерживать `net48` или быть изолирована в `net8`-only implementation. Серверные библиотеки не обязаны поддерживать `net48`, потому что сервер не загружается в Revit. Особенно осторожно с EF Core, gRPC, auth SDK и современными Microsoft.Extensions пакетами внутри клиента.

### Данные пользователя

Локальная БД не должна теряться при обновлении smartCon. Нужны миграции схемы с первой версии.

### IP и лицензии

Семейства являются интеллектуальной собственностью. Даже если публичный каталог появится позже, модель видимости и ownership нужно заложить заранее.

### Корпоративная безопасность

Нельзя хранить токены в обычном JSON. Нужен Credential Manager или иной защищённый storage.

## 16. Открытые вопросы

1. Будет ли локальная БД хранить сами `.rfa` или только индексировать внешние пути?
2. Нужен ли режим «копировать семейства в managed storage» при импорте?
3. Какие типы контента попадут в MVP: только loadable families или сразу schedules/drafting views/system families?
4. Какой минимальный набор metadata обязателен для Verified?
5. Нужна ли связь FamilyManager с PipeConnect уже в MVP или позже?
6. Как корпоративный клиент будет подключать сервер: URL плюс токен, AD/SSO, VPN/self-hosted?
7. Нужна ли публичная база smartCon или только private/organization каталоги?
8. Кто будет модератором публичного контента, если появится marketplace?
9. Нужно ли поддерживать Revit version upgrade на сервере или только хранить версии как есть?
10. Какие метрики success считать первыми: импортированные семейства, поиски, загрузки в проект, повторное использование, снижение ручных загрузок?

## 17. Рекомендуемые первые решения

1. Принять FamilyManager как отдельный module project, а не расширение PipeConnect.
2. Сначала реализовать dockable panel infrastructure.
3. Сразу заложить `IFamilyCatalogProvider`.
4. MVP делать на локальном provider.
5. Каталог хранить вне `.rvt`.
6. Project links, history, favorites и usage хранить в локальной/серверной БД, а не в `.rvt`.
7. В модель сразу добавить `Visibility`, `Status`, `Version`, `SourceProvider`.
8. Не делать AI до появления качественных metadata.
9. Не делать сервер до стабильного local workflow.
10. Для каждой фазы писать отдельный ADR и отдельный технический план.

## 18. Итоговая концепция фаз

| Фаза | Название | Главный результат |
| --- | --- | --- |
| 0 | Foundation | Архитектурные ADR, dockable lifecycle, доменная модель |
| 1 | Local Catalog MVP | Пользователь импортирует, ищет и загружает свои `.rfa` |
| 2 | Rich Metadata | Параметры, типоразмеры, теги, сохранённые поиски |
| 3 | Project Integration | Связь каталога с активным проектом, outdated/deprecated checks |
| 4 | Team Catalog | Shared storage, sandbox, простые роли |
| 5 | Remote Provider | HTTP/server catalog, auth, organization scope |
| 6 | Governance | Workflow, quality rules, review, заявки, аудит |
| 7 | Enterprise Platform | SSO, analytics, API, integrations, lifecycle management |
| 8 | AI Layer | Semantic search, рекомендации, metadata assistant |

Правильная стратегия: построить маленький, быстрый, полезный FamilyManager для себя и отдельных пользователей, затем превратить его в team catalog, потом в enterprise content governance platform. Так smartCon сможет вырасти из набора Revit-инструментов в платформу управления BIM-контентом.
