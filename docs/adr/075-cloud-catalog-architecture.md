# ADR-075: Cloud Catalog Architecture — publish/subscribe sync поверх локальных каталогов

**Date:** 2026-08-25 (актуализировано 2026-09-11 — факты приведены к v2.1.0: FHV22, схема V38, routing вне хэша)  
**Status:** proposed  
**Related:** ADR-015 (Published Storage), ADR-016 (ReadOnly files), ADR-022 (RBAC), ADR-045 (project binding в БД), ADR-049/056/069 (content hash FHV), ADR-054 (Actualization Engine), ADR-058 (min_plugin_version), ADR-071/072/073 (content-hash hierarchy / World B routing / FHV21 segments — v2.1.0), ADR-076 (Security), ADR-077 (Multi-author sync), I-09, I-14, I-16  
**Implements:** roadmap `docs/family-manager/00-strategy/00-familymanager-concept-roadmap.md` §12 (Server strategy), Phase 5 (Remote Provider); backlog ADR-FM-006 «Provider abstraction»  
**Детальный план:** [`docs/family-manager/02-plans/cloud-catalog-master-plan.md`](../family-manager/02-plans/cloud-catalog-master-plan.md)

## Context

FamilyManager сегодня — локальные и SMB-каталоги (ADR-015/022): SQLite + managed
storage, single-writer, offline-first. Следующая продуктовая фаза — **Облачный
каталог**: пользователь публикует свой локальный каталог на центральном сервере,
другие пользователи подключаются к нему (публично или по ключу доступа) и получают
обновления. Первый целевой сценарий — корпоративный («компания публикует,
сотрудники подписываются»), второй — публичный маркетплейс с платными каталогами.

Ключевые факты существующей системы, определяющие решение:

1. **Один файл на версию семейства.** Multi-Revit строки
   (`UNIQUE(catalog_item_id, version_label, revit_major_version)`) — legacy-read
   наследие схемы; новые импорты кладут один плоский файл на версию
   (`StoragePathResolver`, «New flat path»). Умножения объёма на версии Revit
   нет ни локально, ни в облаке. **Тип файла зависит от `family_source`:**
   loadable → `.rfa`; **system → staged мини-проект `.rvt`** (ADR-027/062).
   Манифест обязан различать эти случаи явно (`fileKind`).
2. **FHV22 content hash** уже решает дедупликацию и детект изменений контента
   (rename-invariant, ADR-049). С v2.1.0 хэш иерархичен (ADR-071): 13
   loadable-секций с построчными `section_hashes/section_strings` (V33) и
   per-type хэши `family_type_hashes` (V32) — контентный diff читается из БД
   без открытия Revit. Routing с FHV20 в хэш не входит (World B, ADR-072/073):
   трассировка — item-level данные каталога (`item_routing_rules` V37,
   `family_segment_rules` V38), дедуп по `content_hash` означает «файл без
   трассировки».
3. **Write-путь жёстко локальный:** импорт-pipeline пишет через
   `LocalCatalogDatabase` + `StoragePathResolver`; `IWritableFamilyCatalogProvider`
   оперирует локальными путями. «Удалённый writable provider» потребовал бы
   переписывания всего импорт-pipeline и сломал бы offline-first.
4. Read-сторона уже абстрагирована: `IFamilyCatalogProvider`,
   `CatalogProviderKind.Remote/Corporate/PublicReadOnly` зарезервированы в enum.
5. HTTP-инфраструктура из Revit add-in решена: `GitHubUpdateService`
   (retry, net48 `ServicePointManager` хаки, прогресс) — production-референс.
6. Механизм версионных контрактов данными есть: `database_meta.min_plugin_version`
   (ADR-058), `hash_format_version` гейт.
7. Объём доминируют `.rfa` файлы; каталог ~1000 семейств ≈ ~1 ГБ. Egress —
   главная статья расходов облачной раздачи (AWS S3 $0.09/ГБ vs Cloudflare R2 $0).

Открытые продуктовые вопросы roadmap §16 (6–8) отвечены владельцем: центральный
сервер под управлением AGK (старт — домашний ПК, Docker-first, миграция на VPS
без смены endpoint); монетизация — ключи доступа, продажа ключей авторами вне
платформы на первой фазе; корпоративный сценарий идёт первым.

## Decision

### 1. Облако = sync-слой поверх локальной БД, а не «ещё один writable provider»

Публикация и подписка — **репликация** локального каталога ↔ сервер по модели
git: `publish` = push, `subscribe` = clone + pull. Весь UI и логика FamilyManager
продолжают работать с локальной `catalog.db` (offline-first сохраняется).
Облачный код не трогает импорт-pipeline и не расширяет
`IWritableFamilyCatalogProvider` — он читает локальную БД и пишет в локальную
копию подписчика.

Отклонено: «RemoteCatalogProvider как writable backend» (переписывание
импорт-pipeline, потеря offline, Revit-зависимые артефакты — GLB/avatar —
генерируются локально и не могут быть серверными).

### 2. Publish Point — атомарный неизменяемый снапшот каталога

Каждая публикация создаёт **publish point**: монотонный `publish_seq` (BIGINT,
per-catalog) + неизменяемый **манифест** `smartcon.cloud.catalog-manifest`
(formatVersion=1) — полный сериализованный образ каталога: категории/атрибуты/
правила (совместимо с metadata package v4, ADR-070), items, versions (с
`content_hash`/`hash_format_version`), types, extracted attributes, facts,
dependencies (ADR-066), ссылки на файлы по SHA-256. Каждая версия несёт
`fileKind` (`rfa` для loadable, `stagedRvt` для system) и `sourceRevitVersion`;
карточка каталога агрегирует диапазон `sourceRevitVersion` — подписчик заранее
видит Revit-совместимость файлов (новый Revit открывает старые файлы апгрейдом,
старый Revit файлы новых версий — нет).

Подписчик строит локальную `catalog.db` из манифеста детерминированным импортом.
Хэш всей базы не нужен: diff гранулярен — `(publish_seq)` для «есть ли
изменения», `(item id, version label, content_hash)` для «что именно изменилось».
Перенос v2.1.0-данных (routing World B V36–V38, per-type/section хэши V32/V33)
в манифесте — открытый вопрос план §13.19, решение до C2.

### 3. Content-Addressable Storage (CAS) для файлов

Каждый файл (`.rfa`, assets, avatar) хранится на сервере под ключом
`objects/sha256/{hex}`. Это даёт: дедупликацию между каталогами и версиями,
бесплатный delta-download (клиент качает только отсутствующие хэши),
иммутабельность (бесконечное CDN-кэширование без инвалидации), согласованность с
hash-addressed философией локального кэша (tech-stack §6). Chunking/bsdiff
(casync/OSTree-паттерн) сознательно откладывается: `.rfa` — сотни КБ … единицы
МБ, файл целиком — достаточная дельта; оптимизация — отдельный ADR при росте
egress-издержек подписчиков.

### 4. Раздельные пути доставки: API и файлы

- **API + манифесты** (JSON, килобайты) — через основной сервер
  (фаза 0: домашний ПК + Cloudflare Tunnel).
- **Файлы** (`.rfa`, МБ) — **только через объектное хранилище с presigned URL**.
  Клиент никогда не скачивает файлы через API-сервер.

Мотивация: (а) бесплатный тариф Cloudflare по условиям CDN ограничивает раздачу
«непропорционального объёма больших файлов» — JSON через туннель легален,
гигабайты `.rfa` — риск; (б) канал домашнего ПК не нагружается; (в) переезд на
VPS не трогает файлы вообще. Безопасность presigned-модели — ADR-076 §4.

### 5. Серверный стек (верифицировано 2026-08-25, см. план §14)

- **ASP.NET Core 10** (LTS до 2028-11; .NET 8 LTS истекает 2026-11 — новые
  проекты на .NET 8 не начинаем). Сервер не обязан поддерживать net48 — это
  клиентское ограничение.
- **PostgreSQL 18** + Npgsql 10 / EF Core 10. Серверная схема **расширяет
  локальную модель, не переименовывая её** (принцип tech-stack §13): catalog_,
  family_, publish_points, access_keys, subscriptions, download_events.
- **Объектное хранилище: Cloudflare R2** (S3-compatible, egress $0, free tier
  10 ГБ) через AWSSDK.S3 **только на сервере** (S3 SDK в Revit-клиенте запрещён —
  tech-stack avoid-list). **MinIO запрещён**: CE в maintenance mode с 2025-12,
  репозиторий заархивирован 2026-04, бинарники не публикуются с 2025-10.
  Self-hosted enterprise-вариант (фаза C6): **SeaweedFS** (Apache 2.0,
  production-ready) за тем же S3-интерфейсом.
- Поиск по каталогам: PostgreSQL FTS/`pg_trgm`. OpenSearch — только
  enterprise-фаза (tech-stack §13).
- Всё в **Docker Compose** с первого коммита: api + postgres (+ cloudflared
  sidecar). Инвариант переносимости: ни одна настройка не привязана к железу;
  переезд = перенос volumes БД + смена DNS/туннеля, endpoint неизменен.

### 6. Модель обновлений подписчика

Pull-модель, кнопка **«Обновить»** существующей панели расширяется: для
cloud-подключённой БД сначала выполняется pull (manifest/latest → diff →
докачка недостающих SHA-256 → атомарное применение к локальной копии), затем
обычный refresh UI. Плюс фоновая проверка `publish_seq` (дешёвый HEAD/ETag) с
бейджем «Доступны обновления» (паттерн StatusNotice, ADR-066). Push-уведомлений
(WebSocket/SSE) нет — pull достаточен для каталогов, меняющихся раз в дни.

### 7. Локальная копия подписчика

Физически — обычная локальная БД FamilyManager в отдельном `{db-root}`, с
`CloudLink`-маркером (endpoint, catalogId, slug, role, lastSyncedPublishSeq)
на подключении в `registry.json` и принудительным `Mode=ReadOnly` вне
sync-сеанса (механика `SetWriteAccess`, I-14; хук применения ReadOnly после
`SwitchToPath` — обязателен). Sync — единственный writer, через существующие
репозитории.

**На подписанной копии запрещены:**

- **Database Actualization Engine (ADR-054)** — целиком. Состояние копии
  определяется только манифестом; задачи актуализации мутируют БД и staged
  файлы (ES-маркеры, пересчёт хэшей) → sha256 расходится с манифестом →
  verify-гейт sync (план, E15) объявил бы копию «повреждённой» → бесконечный
  цикл wipe/pull. Гейт устаревших форматов — `hashFormatVersion` манифеста
  (E7) + hash-epoch операция `rehash` (ADR-077 §3c) для легальной смены
  формата хэша через облако.
- **`LocalCatalogMigrator` на Subscribed-копии работает в additive-режиме:**
  DDL-миграции схемы после апгрейда плагина выполняются (схема должна
  соответствовать коду), но data-задачи — только через манифест. Applier
  всегда строит БД текущей схемы; verify не считает DDL-миграции tampering.
- Все write-флоу UI (импорт, переименование, теги, редактор категорий, purge,
  RBAC-админка, assets-операции, SetActiveVersion, overwrite type catalog,
  metadata-import, attribute library) — гейтятся на уровне команд
  (CanExecute), не на уровне падения SQLite (полный список: план §7.3.8).
  Конвертация General↔Project и привязка к проекту на Subscribed-копии
  гейтятся (`base_type`/`project_binding_json` — серверная территория
  манифеста).
- GLB-превью и avatar **не пишутся в копию**: генерируются лениво в общий
  writable кэш `%APPDATA%\SmartCon\FamilyManager\cloud-cache\previews\{sha256}\`
  (файлы только, без строк в БД копии; резолвер превью проверяет кэш первым;
  `glb_state` едет в манифесте, чтобы «нет геометрии» не перегенерировалось).

**Композиция write-access (единый authority):** итоговый write-доступ БД =
AND гейтов (RBAC-роль, compat-гейт ADR-058, pending-actualization гейт,
`CloudLink.Role=Subscribed`). Реализуется расширением
`IDbAccessControlService` cloud-флагом; прямые вызовы `SetWriteAccess(true)`
вне sync-сеанса запрещены (включая `InvalidateCache()`); read-запросы
репозиториев обязаны использовать read-коннекшены, а не
`CreateWritableConnection()` (инвентаризация — аудит-чеклист C2); auto-register
`db_users` на Subscribed-копии отключён.

**Работают без изменений (верифицируется аудит-чеклистом фазы C2, план
§11/C2):** поиск, дерево, загрузка в проект (loadable и system — цепочка
staged `.rvt` → isolation project → load), stale detection, presence.

### 8. Мульти-авторство

Owner + Editors публикуют в один каталог по протоколу **pull-before-push** с
per-item optimistic concurrency — детально ADR-077.

## Consequences

- Клиент: новые интерфейсы в Core (`ICloudCatalogApi`, `ICloudAuthService`,
  `ICloudPublishService`, `ICloudSyncService`, `ICatalogManifestBuilder`,
  `ICatalogManifestApplier`, `ICredentialStore`) и реализации в
  `SmartCon.FamilyManager/Services/Cloud/`. Dependency rule не
  нарушается: HTTP — из FamilyManager (как GitHubUpdateService из Revit-слоя),
  Core — только контракты и DTO.
- Новый серверный репозиторий/solution `SmartCon.Cloud` (ASP.NET Core 10),
  живёт отдельно от Revit-плагина, собирается/деплоится независимо.
- Assets — opt-in при публикации (флаги в манифесте); **GLB-превью НЕ
  публикуются** — подписчик генерирует их лениво локально в кэш вне копии
  (пайплайн ADR-042 существует; §7). **Запланированное исключение:** для
  store-каталогов (фаза C7, магазин) GLB публикуются автором — иначе 3D-превью
  до покупки невозможно (план §11/C7).
- Stale detection (ADR-030) работает на подписанной копии (маркеры ES живут в
  проектах пользователя, не в копии); actualization engine на подписанной
  копии **отключён** (см. §7).
- `min_plugin_version` механика (ADR-058) распространяется на манифест:
  `minPluginVersion` в publish point → устаревший плагин подписчика получает
  read-only + баннер «обновите SmartCon».
- Known limitation: сервер — новая ops-ответственность (бэкапы PG, мониторинг,
  ключи R2). Фаза 0 без SLA.
- Known limitation: удаление publish points (GC) — только «снять с публикации»
  целиком; точечное удаление версий из истории публикаций не поддерживается
  (иммутабельность снапшотов).
- Known limitation: первый subscribe качает весь активный срез каталога
  (нет «скачать только категорию X»; частичная подписка — post-MVP).

## Verification

Заполняется по завершении фаз (см. мастер-план §11): спайк C0 (docker compose +
туннель + R2 presigned round-trip из net48-клиента), билды всех поддерживаемых
конфигураций R19–R27 (2019–2024 net48, 2025–2026 net8, 2027 net10 — матрица
AGENTS.md), unit + integration сьюты, ручной тест владельца на двух машинах.
