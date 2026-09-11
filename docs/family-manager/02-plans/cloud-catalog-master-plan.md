# Cloud Catalog — мастер-план реализации

> **Статус:** draft → pending validation | **Дата:** 2026-08-25
> **Актуализировано:** 2026-09-11 — ветка rebase на v2.1.0; факты приведены к текущему коду main (FHV22, схема V38, routing вне хэша — World B, Revit 2019–2027). ADR перенумерованы 071/072/073 → 075/076/077 (номера заняты в main решениями v2.1.0).
> **ADR:** [075](../../adr/075-cloud-catalog-architecture.md) (архитектура), [076](../../adr/076-cloud-catalog-security.md) (безопасность), [077](../../adr/077-cloud-catalog-multi-author-sync.md) (мульти-автор)
> **Исполнитель:** один ИИ-агент. Документ написан как полное ТЗ: контекст, контракты, файлы, фазы, критерии приёмки, запреты.
> **Реализует:** roadmap `../00-strategy/00-familymanager-concept-roadmap.md` Phase 5 (Remote Provider) + Phase 7 (marketplace, частично).

---

## 0. Как читать этот документ (для исполнителя)

1. До старта любой фазы исполнитель обязан загрузить: `AGENTS.md`, `docs/invariants.md`, `docs/architecture/dependency-rule.md`, `docs/family-manager/README.md`, ADR-075/076/077, skill `smartcon-build-guide`, skill `smartcon-logging`, skill `smartcon-testing`.
2. Нумерация «C-фаз» (C0–C6) продолжает продуктовый roadmap; это НЕ Phase 37+ основной линии. Каждая фаза завершается: сборка всех поддерживаемых конфигураций R19–R27 (0/0; 2019–2024 net48, 2025–2026 net8, 2027 net10 — матрица AGENTS.md), unit-тесты зелёные, валидация субагентом `general`, ручной тест владельца (для UI), changelog-кандидат.
3. Любое отступление от плана — через вопрос владельцу (`question` tool), не молча.
4. Имена интерфейсов/классов в плане — **канонические**: исполнитель использует их как есть. Если по ходу выясняется, что имя конфликтует с существующим — согласовать переименование.

---

## 1. Бизнес-цель и сценарии

**Цель:** превратить FamilyManager из «локального менеджера семейств» в платформу распространения BIM-контента — киллер-фича SmartCon. Конкурентная рамка: UNIFI/Kinship (командные библиотеки, подписка) и BIMobject/MEPcontent (каталоги производителей). Наше отличие: каталог публикует **любой** пользователь в один клик из существующей локальной базы, с валидацией качества (ADR-059) и умными зависимостями семейств (ADR-066), а подписчики получают обновления как в git pull.

### Сценарий S1 — корпоративный (ПЕРВЫЙ, фазы C0–C4)

Компания «ВентПроект»: BIM-мастер ведёт локальную базу арматуры → регистрирует аккаунт SmartCon Cloud → «Опубликовать базу» (или создаёт пустую облачную базу и импортирует по одному) → генерирует ключи и рассылает **приглашения** (одна строка: endpoint+slug+ключ) 15 инженерам → каждый инженер: «Подключить облачный каталог» → регистрация аккаунта (email+пароль, consent) → вставка приглашения → первый pull → каталог в панели, семейства грузятся в проекты → BIM-мастер обновляет отвод → «Опубликовать изменения» (ПКМ) → у всех бейдж «Доступны обновления» → «Обновить» → докачались 2 МБ вместо 1 ГБ. Два BIM-мастера правят разные семейства одновременно — обе правки публикуются без конфликтов (ADR-077).

### Сценарий S2 — маркетплейс (фаза C5)

Автор «МЕП-Библиотека РФ» публикует платный каталог → продаёт ключи покупателям любым способом (свой сайт/Boosty/лично) → покупатель вводит ключ в SmartCon → получает каталог и обновления. Автор видит статистику скачиваний. Платформа не участвует в сделке (не merchant of record) — юридически чисто.

### Сценарий S3 — публичные бесплатные каталоги (фаза C5)

Открытый листинг без ключа: подключение в один клик из витрины. Сидирование маркетплейса: 2–3 эталонных каталога от AGK на старте (проблема пустого рынка).

**Non-goals (явно):** real-time co-editing; серверная обработка `.rfa` (на сервере нет Revit и не будет); хранение нескольких Revit-версий одной версии семейства (см. §3); встроенные платежи в C0–C4; публичный веб-портал до C5.

---

## 2. Глоссарий (новые термины — добавить в `docs/domain/glossary.md` при реализации)

| Термин | Определение |
|---|---|
| **Cloud Catalog** | Локальная БД FamilyManager, связанная с серверным каталогом (опубликованная или подписанная) |
| **Publish Point** | Атомарный неизменяемый снапшот каталога: `publish_seq` + манифест + набор файлов |
| **Manifest** | JSON-документ `smartcon.cloud.catalog-manifest` v1 — полный сериализованный образ каталога |
| **CAS** | Content-Addressable Storage: файлы адресуются SHA-256 содержимого |
| **Access Key** | Ключ доступа подписчика к каталогу (`SCCAT-…`), выпускается автором |
| **Subscriber** | Пользователь, подключивший чужой каталог по ключу (read-only локальная копия) |
| **Owner / Editor** | Серверные роли публикации (ADR-077 §1) |
| **Delta (publish)** | Список изменений от `basePublishSeq`: upsert/deprecate/delete items и версий |
| **Tombstone** | Маркер удаления item/версии в publish point |
| **Check-in** | Периодическая онлайн-валидация подписки платного каталога (ADR-076 §3) |
| **Remote source** | Маркер в локальной БД подписчика: endpoint + catalogId + lastSyncedPublishSeq |
| **Пак** | Синоним «store-каталога»: пак = облачный каталог, выставленный в магазин. Не отдельная сущность — entitlements привязаны к `catalog_id` пака |
| **Баллы (credits)** | Внутренняя валюта магазина: зарабатываются публикацией валидных уникальных семейств, тратятся на скачивание. Не выводятся в деньги (не электронные деньги — юридически цифровой товар) |
| **Entitlement** | Право пользователя на файлы item (поштучно, lifetime) или пака (annual); проверяется в `/files/resolve` per-item |
| **Preview-подписка** | Подписка «витрина без файлов»: манифест+assets доступны, файлы — только по entitlement |

---

## 3. Существующий фундамент (что НЕ пишем заново)

| Компонент | Где | Использование в Cloud |
|---|---|---|
| FHV22 content hash (ADR-071: 13 loadable-секций, per-type `family_type_hashes` V32, `section_hashes/section_strings` V33) | `SmartCon.Core/.../FamilyContentHasher.cs` | Идентичность контента версии в манифесте и diff; секционные/per-type хэши — кандидат в манифест (§13.19) |
| `IFamilyCatalogProvider` + `CatalogProviderKind.Remote/Corporate/PublicReadOnly` | Core | Read-контракт; enum-значения уже зарезервированы — НЕ добавлять новые |
| `LocalCatalogDatabase`, `SetWriteAccess`, DELETE journal | FamilyManager | Локальная копия подписчика (I-14): read-only вне sync |
| `StoragePathResolver`, flat `.rfa` layout | FamilyManager | Структура файлов локальной копии = структуре любой БД |
| `DatabaseConnection` + `registry.json` (multi-DB) | FamilyManager | Расширение `Kind`: добавить `CloudPublished`/`CloudSubscribed` |
| Metadata package v4 сериализация | `CategoryTreeEditorViewModel.ExportImport.cs` | Образец wire-формата; секция `categories/attributes/bindings/rules` манифеста |
| Actualization Engine (ADR-054) | FamilyManager | Паттерн «detect pending → apply → clear» для sync-движка; применение манифеста к копии — те же репозитории |
| `GitHubUpdateService` | `SmartCon.Revit/Updates/` | **Референс HTTP-клиента**: retry, net48-хаки, прогресс, staging. Не наследовать — писать `CloudCatalogApiClient` по образцу |
| `min_plugin_version` гейт (ADR-058) | FamilyManager | `minPluginVersion` в манифесте → тот же баннер «обновите плагин» |
| StatusNotice / StatusDetailsView (ADR-066) | FamilyManager/UI | Диалоги «доступны обновления», отчёт о sync, конфликты публикации |
| RBAC локальная (ADR-022) | FamilyManager | Не меняется; облачные роли — отдельная модель (ADR-076 §1) |
| GLB pipeline (ADR-042), avatar (ADR-047) | FamilyManager | Ленивая локальная генерация превью на подписанной копии |
| CAS-пул 3D-превью `files/_shared/models/{shard2}/{view3dHash}.glb` (v2.1.0, #249) | FamilyManager | Паттерн иммутабельного шаринга GLB между семействами/типами; при opt-in публикации превью (store, C7) серверный `files/resolve` обязан учитывать кросс-item переиспользование объектов пула |
| Комплаенс-проверка каталога (ADR-074, #259) | FamilyManager | Pure SQLite без Revit и открытого документа — работает на read-only Subscribed-копии без изменений; ещё один «читающий» сервис для аудит-чеклиста C2 |

**Важный факт (подтверждён владельцем 2026-08-25, уточнён ревью):** на версию семейства хранится **ровно один файл**: `.rfa` для loadable, **staged мини-проект `.rvt`** для system families (ADR-027/062; `CatalogActualizationService`, `FamilyBatchImportViewModel`: `"system" => ".rvt"`). Строки `catalog_versions` с разными `revit_major_version` на один label — legacy-read наследие (schema `UNIQUE(catalog_item_id, version_label, revit_major_version)`, `GetRevitFileDirectory`, doc-comment «multi-Revit labels» в `IFamilyCatalogProvider`). **Техдолг (отдельная маленькая задача до C2):** пометить в комментариях кода, что multi-Revit — legacy-read only, чтобы агенты не проектировали от него. Манифест несёт `sourceRevitVersion` одной версии как факт происхождения + явный `fileKind` (`rfa`|`stagedRvt`), без массива файлов per-Revit.

**Изменения v2.1.0, которые план обязан учитывать (актуализация 2026-09-11):**

1. **Routing покинул content-хэш (FHV20, World B — ADR-072):** трассировка — item-level данные каталога (`item_routing_rules`/`item_routing_type_settings` V37; legacy-канал `family_routing_rules` V34 заморожен), а **сегментная конфигурация мини-проекта версионирована** (`family_segment_rules` V38, входит в SEGMENTS-секцию хэша — FHV21/ADR-073). Манифест обязан переносить обе группы данных (см. §13.19).
2. **Staging мини-проектов ручной** (ADR-072): без `CopyElements`, мини без фитингов; рассуждения старого текста плана о RoutingPreferences-фантомах в staged-файле устарели — источником правды о трассировке является БД (`SegmentRuleComposition`).
3. **Схема локальной БД — V38** (на момент написания плана была V31): V32 `family_type_hashes`, V33 `section_hashes/section_strings`, V34–V38 — routing/segments-таблицы World B. Новые cloud-миграции нумеруются с V39.
4. **Revit 2027 / .NET 10** в матрице клиента (R27; `SmartCon.Tests` на net8 — не собирать под R27, см. AGENTS.md); новый серверный код под BSL обязан не конфликтовать с ALC-изоляцией зависимостей (ADR-051).

---

## 4. Архитектура

```
        Cloudflare (TLS, WAF, DDoS)
                 │  tunnel (только API+JSON)
                 ▼
┌─ SmartCon.Cloud Server (Docker Compose) ──────────────┐
│  api: ASP.NET Core 10 (REST+OpenAPI, JWT, rate limit) │
│  db:  PostgreSQL 18 (метаданные, ключи, подписки)     │
│  cas: Cloudflare R2 (S3 API, presigned URL, egress=0) │
└────────────────────────────────────────────────────────┘
        ▲ publish (JWT, delta)          ▲ subscribe/sync (JWT)
┌───────┴──────────┐          ┌─────────┴──────────────┐
│ Плагин АВТОРА    │          │ Плагин ПОДПИСЧИКА      │
│ локальная БД rw  │          │ локальная копия ro     │
│ CloudPublishSvc  │          │ CloudSyncSvc + «Обновить»│
└──────────────────┘          └────────────────────────┘
        │ presigned PUT в R2; фолбэк через api: download — C2, upload — C4 │
        └────────────────────► R2 ◄──── presigned GET ───┘
```

Разделение путей (ADR-075 §4): API — маленький JSON через сервер; файлы — напрямую из R2 по presigned URL (15 мин, GET). Клиентский S3 SDK запрещён (tech-stack avoid-list): клиент ходит только в наш API; presigned URL для него — opaque строка для `HttpClient.GetAsync`.

---

## 5. Wire-формат: манифест v1

`format: "smartcon.cloud.catalog-manifest"`, `formatVersion: 1`. Сериализация System.Text.Json, camelCase, unknown properties игнорируются (forward-compat, как metadata package v4). Полный образ каталога на publish point:

```jsonc
{
  "format": "smartcon.cloud.catalog-manifest",
  "formatVersion": 1,
  "catalogId": "guid",
  "publishSeq": 42,
  "publishedAtUtc": "2026-08-25T12:00:00Z",
  "publishedBy": "BIM-отдел ВентПроект",   // displayName, НЕ email (PII, ADR-076 §4)
  "minPluginVersion": "2.1.0",        // семвер; гейт ADR-058 у подписчика
  "hashFormatVersion": 22,            // FHV22; несовпадение → sync отклонён с баннером
  "revitVersionRange": { "min": 2021, "max": 2025 },  // агрегат по sourceRevitVersion — гейт совместимости (ниже)
  "meta": {                           // metadata-слой = metadata package v4 как есть
    "categories": [ /* v4 */ ], "attributes": [ /* v4 */ ],
    "bindings": [ /* v4 */ ], "assignmentRules": [ /* v4 */ ]
  },
  "items": [{
    "id": "guid", "name": "Отвод 90° стальной", "normalizedName": "...",
    "categoryPath": "Трубопроводы > Отводы",   // путь, не id — переносимо; " > " — формат CategoryTree.BuildFullPath (единый с v4-пакетом)
    "contentStatus": "Active",                 // Active|Deprecated (Retired→Deprecated на границе, known-workarounds)
    "familySource": "loadable",                // loadable|system
    "revitCategoryId": -2008055,               // BuiltInCategory ordinal (FHV3-философия, локале-инвариант)
    "familyKey": null,                          // system-семейства: ADR-064 (обязателен для system)
    "currentVersionLabel": "v3",
    "tags": ["сталь", "ДУ50"],
    "facts": [ { "key": "PartType", "valueKey": "23", "valueDisplay": "Тройник" } ],  // item-level — схема family_facts (PK item+key)
    "versions": [{
      "versionLabel": "v3",
      "contentHash": "A1B2…",                 // FHV{hashFormatVersion}
      "sectionHashes": { "META": "…", "GEOM": "…" },  // V33 (ADR-071) — кандидат в v1, см. §13.19
      "sourceRevitVersion": 2023,              // факт происхождения (один файл!)
      "fileKind": "rfa",                       // "rfa" (loadable) | "stagedRvt" (system)
      "typesCount": 12, "parametersCount": 40,
      "publishedAtUtc": "…", "publishedBy": "…",
      "glbState": null,                            // null|-1 терминальный «нет геометрии» (#157) — иначе копия перегенерирует
      "nestedSharedFamilies": [ "Болт М12" ],        // V13, workaround REVIT-198137 — массив строк (wire-формат v1, реализация 2026-09-11)
      "file": { "sha256": "…", "sizeBytes": 843210, "fileName": "Отвод 90°.rfa" },
      "types": [ { "typeName": "ДУ50", "parameters": [ /* extracted values, storage-type-typed */ ] } ],
      "dependencies": [ { "childItemId": "guid", "kind": "routing", "childVersionLabel": "v1",
                          "partName": "Отвод", "ordinal": 0 } ]  // partName/ordinal нужны resolver'у трассировки (V29)
    }],
    "assets": [                                 // opt-in; GLB-превью НЕ публикуются (генерятся локально)
      { "sha256": "…", "sizeBytes": 1, "assetType": "Image", "fileName": "photo.png",
        "isPrimary": true, "versionLabel": null, "description": "" }
    ],
    "avatar": { "sha256": "…", "sizeBytes": 51234 }   // производный avatar.png, если есть
  }],
  "removed": [ { "itemId": "guid", "versionLabel": "v2" } ]   // tombstones (ADR-077 §6)
}
```

**System families в манифесте:** для `familySource:"system"` версия несёт
`fileKind:"stagedRvt"` + `file` указывает на staged `.rvt`; `familyKey`
(ADR-064) обязателен; `types[]` содержит системные типы (как в `family_types`
с `family_key`). Подписчик применяет такую версию через существующую цепочку
load-флоу системных семейств (isolation project, ADR-061/062) — верифицируется
обязательным интеграционным тестом C2 «pull system family → load в Revit из
подписанной копии».

**Revit-совместимость файлов:** `revitVersionRange` + per-version
`sourceRevitVersion`. Файл, созданный в Revit N, открывается в Revit ≥ N
(апгрейд на лету) и **не открывается** в Revit < N. Клиент при subscribe
сравнивает с установленными у пользователя версиями Revit и показывает явное
предупреждение («N семейств требуют Revit 2025+, у вас 2023»); в витрине C5
диапазон — обязательный атрибут карточки. Серверной конверсии нет и не будет
(non-goal §1).

**Hash epoch (`kind:"rehash"`):** publish point специального вида — полный
пересчёт `contentHash` всех версий при смене `hashFormatVersion`, без новых
файлов, с сохранением item id (ADR-077 §3c). Подписчики на старом формате
получают E7-гейт и остаются на предыдущем publish point.

Правила сериализации:
- Значения параметров — в **internal units + исходный `unit_type_id`/spec** (как `extracted_attribute_values` в SQLite), конвертация — на клиенте (I-02).
- Имена категорий/атрибутов — строками (переносимо между базами, как в пакете v4); id внутри манифеста стабильны в пределах каталога.
- **Routing-данные World B (v2.1.0):** item-level трассировка (`item_routing_rules`/`item_routing_type_settings`, V37), per-version сегментные правила (`family_segment_rules`, V38) и таблицы размеров сегментов (`family_segment_sizes`, V36) — контент каталога, без которого подписчик теряет вкладку «Трассировка» и загрузку системных семейств с конфигурацией; включаются в item/version-секции манифеста (детали — §13.19, решение до C2).
- **Публикуются только активные версии** (решение владельца 2026-09-11): `versions[]` манифеста несёт версии **только одного label** — `currentVersionLabel` («облачный срез»); все Revit-варианты этого label едут отдельными записями (файл r2021 не откроется в R2025+ без старшего варианта — подписчик на любом Revit не должен терять семейство). Локальная история промежуточных версий (черновики между публикациями, экспериментальные итерации) в облако **не попадает** — она живёт только на ПК автора, как локальные резервные копии Revit у пользователя. R2 накапливает все *когда-либо опубликованные* активные версии (CAS-файлы живут, пока на них ссылается живой publish point, §6.5) — «облачные резервные версии» в духе Revit server, хранящего синхронизации всех авторов; см. §13.15. Откат = публикация старого контента **новой версией** (иммутабельность ADR-077 §3b, аналог `git revert`); просмотр/восстановление из истории publish points — post-MVP. Автопубликации нет: публикация — всегда явное действие владельца/редактора.
- **PII-фильтр:** `publishedBy` — только `displayName`; email, `user@machine` локальной RBAC, локальные пути — запрещены в манифесте (ADR-076 §4). `CatalogManifestBuilder` содержит явный whitelist-сериализатор, а не «сериализуем всю строку БД».
- Манифест сжимается gzip на проводе (`Content-Encoding`). **Размер — эмпирический вопрос:** `types[].parameters[]` доминируют (сотни КБ на item с богатыми атрибутами), оценка «50 МБ / 5–10 тыс. семейств» занижена и пересматривается по замеру реальной базы владельца — **обязательный артефакт спайка C0**: замер несжатого/сжатого размера манифеста. Если замер показывает >100 МБ несжатого на целевых базах — формат v1 до C2 расширяется двухчастным манифестом (index: items+hashes для check/diff; payload: types/parameters на sync), это дешевле до заморозки, чем после.
- `formatVersion` bump при ломающих изменениях; читатель обязан отклонять неизвестную мажорную версию с баннером обновления (та же UX-механика ADR-058).

---

## 6. Сервер `SmartCon.Cloud`

Новый solution `server/SmartCon.Cloud.sln` (отдельный от `src/SmartCon.sln`; CI — отдельный workflow). Проекты: `SmartCon.Cloud.Api` (ASP.NET Core 10), `SmartCon.Cloud.Core` (домен/сервисы, тестируемо), `SmartCon.Cloud.Data` (EF Core 10 + Npgsql 10, PG 18), `SmartCon.Cloud.Tests` (xUnit + Testcontainers PG).

### 6.1. Схема PostgreSQL (расширяет локальную модель, не переименовывает)

```sql
cloud_users(id uuid PK, email citext UNIQUE, password_hash text, display_name text,
            status text, created_at_utc timestamptz, last_login_at_utc,
            deleted_at_utc timestamptz NULL,                    -- soft-delete (ФЗ-152, ADR-076 §7)
            consent_version text, consent_at_utc timestamptz);  -- согласие при register
auth_tokens(id uuid PK, user_id FK, kind text,            -- 'refresh' | 'pat'
            token_hash text UNIQUE, scope text, expires_at_utc, revoked_at_utc,
            replaced_by uuid NULL,                           -- refresh rotation
            last_used_at_utc timestamptz NULL);                -- аудит PAT
catalogs(id uuid PK, owner_user_id FK, name text, slug citext UNIQUE,
         description text, visibility text,                -- 'private'|'public'
         pricing text,                                     -- 'free'|'key'
         check_in_interval_days int NULL,                  -- платные: ADR-076 §3 (гейт активируется в C4; до C4 — только хранение)
         moderation_status text NOT NULL DEFAULT 'ok',     -- 'ok'|'hidden'|'verified' (C1 схема, UI — C5)
         current_publish_seq bigint NOT NULL DEFAULT 0,
         min_plugin_version text, hash_format_version int,
         revit_version_min int, revit_version_max int,     -- агрегат из манифестов, для карточки/витрины
         unpublished_at_utc timestamptz NULL,
         sunset_until_utc timestamptz NULL,                -- grace-pull окно после unpublish (E6)
         created_at_utc);
catalog_editors(catalog_id FK, user_id FK, added_by FK, added_at_utc,
                PK(catalog_id, user_id));
publish_points(catalog_id FK, seq bigint, manifest_object_key text,  -- в CAS
               published_by FK, published_at_utc, change_count int,
               PK(catalog_id, seq));
catalog_items_mirror(catalog_id FK, item_id uuid, name text, normalized_name,
               category_path text, content_status text, family_source text,
               revit_category_id bigint, current_version_label text,
               last_modified_publish_seq bigint, last_modified_by FK,
               PK(catalog_id, item_id));                    -- per-item курсор ADR-077 §3
item_versions_mirror(catalog_id FK, item_id FK, version_label text,
               content_hash text, file_sha256 text, size_bytes bigint,
               source_revit_version int, published_by FK,
               PK(catalog_id, item_id, version_label));
item_dependencies_mirror(catalog_id FK, parent_item_id, parent_version_label,
               child_item_id, kind text,                             -- серверная валидация delete (§6.3.8, ADR-077 §6)
               PK(catalog_id, parent_item_id, parent_version_label, child_item_id, kind));
cas_objects(sha256 text PK, size_bytes bigint, first_seen_at_utc);   -- дедуп + GC
publish_point_files(catalog_id FK, seq FK, sha256 FK, role text);    -- 'rfa'|'asset'|'avatar'
access_keys(id uuid PK, catalog_id FK, key_hash text UNIQUE, label text,
            created_by FK, expires_at_utc NULL,               -- дедлайн АКТИВАЦИИ, не подписки (ADR-076 §3)
            max_activations int NOT NULL DEFAULT 1,           -- безлимит = явный выбор автора
            status text, created_at_utc, revoked_at_utc NULL);
subscriptions(id uuid PK, catalog_id FK, user_id FK, access_key_id FK NULL,
              activated_at_utc, last_checkin_at_utc,
              email_consent_at_utc timestamptz,               -- согласие «email виден автору» (ADR-076 §3/§7)
              suspended_at_utc NULL, suspended_reason text NULL,
              status text);                                   -- 'active'|'suspended'
access_key_events(id bigserial PK, key_id FK, actor_user_id FK, action text, at_utc, reason text NULL);      -- append-only аудит (споры)
subscription_events(id bigserial PK, subscription_id FK, actor_user_id FK NULL, action text, at_utc, reason text NULL);
reports(id bigserial PK, catalog_id FK, reporter_user_id FK, reason text, created_at_utc);                 -- модерация (UI — C5)
download_events(id bigserial PK, catalog_id FK, user_id FK NULL,             -- NULL при удалении аккаунта
                sha256 NULL, kind text, at_utc);                              -- stats = distinct users за окно (антинакрутка)

-- Магазин (заготовка схемы — C5; механика — C7). Ledger-only, баланс = агрегат, никогда UPDATE:
credit_ledger(id bigserial PK, user_id FK, delta int NOT NULL,                -- +earn/purchase, −spend/burn
              kind text,                       -- 'earn'|'earn_frozen'|'purchase'|'spend'|'burn'|'unfreeze'
              ref_catalog_id FK NULL, ref_item_id uuid NULL, order_ref text NULL,
              vests_at_utc timestamptz NULL,   -- earn_frozen → доступен после 90 дней (анти hit-and-run)
              created_at_utc);
entitlements(id uuid PK, user_id FK, catalog_id FK, item_id uuid NULL,        -- NULL = весь пак
             kind text,                          -- 'lifetime' (поштучно) | 'annual' (пак на год)
             updates_until_utc timestamptz NULL, -- annual: окно обновлений/новых семейств
             paid_credits int, order_ref text NULL,                             -- аудит/возвраты рельса 2 (C8)
             created_at_utc);
item_prices(catalog_id FK, item_id uuid NULL, price_credits int NOT NULL,     -- NULL item_id = цена пака
            PK(catalog_id, item_id));
storage_profiles(id uuid PK, kind text,                      -- 'builtin_r2'|'external_s3'|'gateway' (BYOS, C6)
                 owner_user_id FK, config_json text NULL, secret_ref text NULL, gateway_url text NULL);
-- catalogs.storage_profile_id FK NULL (default = builtin R2)
-- cas_objects: PK меняется на (sha256, storage_profile_id) — дедуп только внутри профиля хранения (E12 внутри профиля)
-- Глобальный индекс для балльной уникальности: item_versions_mirror(content_hash) cross-catalog
```

Индексы: `catalogs(slug)`, `item_versions_mirror(catalog_id, content_hash)`, `access_keys(key_hash)`, `download_events(catalog_id, at_utc)`. Полнотекстовый поиск публичных каталогов: `pg_trgm` по `catalogs.name/description` + `catalog_items_mirror.name`.

### 6.2. API v1 (контур; полный OpenAPI генерится из кода)

**Матрица visibility × pricing:**

| visibility \ pricing | free | key | store (C7) |
|---|---|---|---|
| **public** | Витрина C5, подписка без ключа | Витрина C5, подписка только с ключом | Магазин: preview всем, файлы — по entitlement (баллы) |
| **private** | Не в витрине; подписка по приглашению без ключа | Не в витрине; только с ключом (корпоративный дефолт S1) | Запрещено (магазин публичен по определению) |

`key` обязателен в `subscribe` только при `pricing='key'`. **`subscribe` идемпотентен:** повторный вызов для существующей `(catalog_id, user_id)` = restore подписки, активацию ключа **не потребляет** (переустановка ОС/новая машина не сжигает `maxActivations=1` — E30). Store-каталог: `subscribe` создаёт **preview-подписку** (без ключа, бесплатно).

**Переходы visibility/pricing при живых подписчиках (PATCH валидация + подтверждение в UI):**

| Переход | Семантика |
|---|---|
| public → private | Существующие подписки **сохраняются** (grandfathered); новые — только по приглашению/ключу; из витрины скрывается |
| private → public | Появляется в витрине (C5), moderation_status='ok' требуется |
| free → key | Существующие подписки сохраняются; **новые** — только с ключом |
| key → free | Новые подписки без ключа; старые ключи продолжают работать |
| ↔ store | **Запрещено при живых подписках/entitlements** (конвертация = новый каталог; подписчики/покупатели не должны терять условия сделки) |

**Переходы visibility/pricing при живых подписчиках (PATCH валидация + подтверждение в UI):**

| Переход | Семантика |
|---|---|
| public → private | Существующие подписки **сохраняются** (grandfathered); новые — только по приглашению/ключу; из витрины скрывается |
| private → public | Появляется в витрине (C5), moderation_status='ok' требуется |
| free → key | Существующие подписки сохраняются; **новые** — только с ключом |
| key → free | Новые подписки без ключа; старые ключи продолжают работать |

Любой переход при `subscriptions > 0` — диалог-подтверждение с числом затронутых.

```
# Auth
POST /v1/auth/register            { email, password, displayName }
POST /v1/auth/login               → { accessToken, refreshToken, user }
POST /v1/auth/refresh             { refreshToken } → ротация (ADR-076 §2)
POST /v1/auth/logout              (revoke refresh)

# Каталоги (автор)
POST   /v1/catalogs               создать (name→slug, visibility, pricing)
GET    /v1/catalogs/mine          мои каталоги (owner/editor)
PATCH  /v1/catalogs/{slug}        настройки: описание, видимость, pricing, check-in
DELETE /v1/catalogs/{slug}        снять с публикации → sunsetting: подписки ОСТАЮТСЯ active на окно 30 дней (финальный pull, E6), потом 410 + GC; unpublished_at_utc/sunset_until_utc
POST   /v1/catalogs/{slug}/editors          добавить редактора (Owner)
DELETE /v1/catalogs/{slug}/editors/{userId}

# Публикация
POST /v1/catalogs/{slug}/publish/prepare   { files:[{sha256,sizeBytes}] } → { missing:[sha256], uploadUrls:{sha256: presigned PUT} }
POST /v1/catalogs/{slug}/publish           { basePublishSeq, kind: "normal"|"rehash", changes:[…], manifest } → 201 { publishSeq } | 409 { conflicts:[…] } | 422 { validationErrors }   // rehash = hash-epoch, ADR-077 §3c
GET  /v1/catalogs/{slug}/publish/{seq}     карточка publish point (автору)

# Ключи (Owner/Editor)
POST   /v1/catalogs/{slug}/keys   { label, expiresAtUtc?, maxActivations? } → { key: "SCCAT-…" } (один раз!)
GET    /v1/catalogs/{slug}/keys   список (без plaintext): метка, статус, активации
DELETE /v1/catalogs/{slug}/keys/{id}   отзыв

# Подписка
POST /v1/catalogs/{slug}/subscribe    { key? } + consent → { subscription, catalog card }   // key обязателен при pricing='key'; лимит проверяется в сериализуемой транзакции
DELETE /v1/catalogs/{slug}/subscribe  отписка
POST /v1/subscriptions/{id}/suspend   { reason } — точечный отзыв (Owner), аудит subscription_events
POST /v1/subscriptions/{id}/resume    снять suspend (Owner)
GET  /v1/catalogs/subscriptions       мои подписки (+ currentPublishSeq, hasUpdates)

# Sync (все catalog-scoped эндпоинты проверяют статус подписки per-request — ADR-076 §2)
GET /v1/catalogs/{slug}/manifest/latest   → { publishSeq, manifestSha256, sizeBytes } (ETag на publishSeq); при sunsetting — { publishSeq, finalSeq, sunsetUntilUtc }
GET /v1/catalogs/{slug}/manifest/{seq}    → манифест (gzip)
POST /v1/files/resolve                    { sha256:[…] } → { urls:{sha256: presigned GET}, unavailable:[sha256] }  // denied ∪ not-found = unavailable (ADR-076 §4)
POST /v1/files/download                   # C2: проксирование скачивания через API — фолбэк для сетей, режущих R2 (E13)
POST /v1/files/upload                     # C4: проксирование заливки через API (тот же фолбэк, E13)
POST /v1/catalogs/{slug}/checkin          платные: продлевает last_checkin (ADR-076 §3; до C4 — заглушка 200, гейт не активен)

# Маркетплейс (C5)
GET /v1/catalogs/discover?q=&category=    публичные листинги (pg_trgm)
GET /v1/catalogs/{slug}                   публичная карточка (без контента)
GET /v1/catalogs/{slug}/stats             автору: подписчики, скачивания, по времени
```

Семантика ошибок (единое правило): `401` (токен); **`404` — только для чужих** (анти-оракул ADR-076 §4: запрос без доступа и без записи подписки → «не существует»); **`403` — для СВОИХ с существующей записью `subscriptions`** (slug им известен, оракула нет): problem+json с кодом `subscription_suspended` / `key_revoked` (клиент показывает различимый текст E36); **`410 Gone`** — каталог снят с публикации и окно sunsetting истекло (в окне — `manifest/latest` отдаёт `{finalSeq, sunsetUntilUtc}`); `409` (конфликт publish, машиночитаемый `conflicts[]`); `422` (валидация манифеста/зависимостей/дедуп/иммутабельность); `429` (rate limit, `Retry-After`). Все ошибки — `application/problem+json`.

### 6.3. Валидация publish на сервере

1. JWT scope + роль (Owner/Editor).
2. `basePublishSeq == catalogs.current_publish_seq` на входе в транзакцию (серьёзная гонка ловится здесь, а не только per-item).
3. Per-item курсоры (ADR-077 §3).
4. Каждый `file.sha256` из манифеста существует в `cas_objects` (publish без файла = 422). Каждый sha256 залитого в prepare, но не вошедшего в commit — orphan, GC.
5. `hashFormatVersion` каталога совпадает; `minPluginVersion` каталога поднимается, если в дельте есть версии, требующие новее (клиент сообщает свой pluginVersion при publish; сервер хранит max).
6. **Дедупликация (ADR-077 §3a):** upsert-version с `content_hash`, существующим в каталоге под другим item_id → 422 `{ duplicateOfItemId }`.
7. **Иммутабельность опубликованных версий (ADR-077 §3b):** upsert-version с существующим `(itemId, versionLabel)` и отличающимся `content_hash` → 422 «создайте новую версию».
8. Dependency-валидация: `delete-item/delete-version`, ломающий ссылку из другого item (по `publish_point_files`+dependencies в манифесте) → 422 (дублирует локальный Dependency Guard, ADR-067).
9. **Store-защита проданного (C7):** `delete-item`/`delete-version` при живых entitlements на этот item → **422** («семейство продано N раз — удаление запрещено, используйте Deprecated»). Покупатель никогда не теряет купленное через tombstone (аналог Dependency Guard для продаж). Атомарный commit: PG-транзакция (mirror-таблицы + publish_points + агрегат `revit_version_min/max`) → манифест в CAS (иммутабельный ключ `manifests/{catalogId}/{seq}.json.gz`) → `current_publish_seq = seq`. Порядок: манифест в CAS **до** коммита PG (если PG упал — манифест-orphan, GC; обратный порядок даст publish point без манифеста).

### 6.4. Upload файлов

MVP: presigned **PUT** прямо в R2 (prepare → uploadUrls, с constraint Content-Length = декларированный `sizeBytes`; per-object max — конфиг). Клиент заливает с retry и **обязан передавать `x-amz-checksum-sha256`** (полный хэш объекта); сервер при commit сверяет checksum **каждого** файла с манифестом + magic-bytes (`rfa`/`rvt` = OLE CFB signature; assets — whitelist расширений) — ADR-076 §4a. Не прошёл → 422, publish отклонён. **Фолбэки через API для сетей, режущих R2 (E13): download (`POST /v1/files/download`) — C2 (первый корпоративный клиент раньше C4), upload (`POST /v1/files/upload`) — C4.** Оба фолбэка идут через туннель → лимит free-прокси **100 МБ на запрос** (§14) действует на оба; файл >100 МБ за блокирующим прокси недоступен — диагностика «файл недоступен за вашим прокси [Action: разрешите *.r2.cloudflarestorage.com]», known limitation §13. **Квоты (C1, ADR-076 §4a):** per-user storage quota (дефолт 5 ГБ; для каталогов на BYOS-профилях — квота не применяется, объём контролирует владелец хранилища), лимит pending-uploads, rate limit на prepare.

### 6.5. GC, бэкапы, жизненный цикл

- `cas_objects` без ссылок из ни одного `publish_point_files` живых publish points → delete из R2 (фоновая задача, ежедневно; окно grace 7 дней на in-flight; против генерации orphans работают квоты §6.4).
- История publish points: хранить все (манифесты маленькие); «снять с публикации» → **sunsetting 30 дней**: подписчики видят `finalSeq` и могут сделать финальный pull в окне (защита покупателя, E6); после окна — 410, манифесты и неиспользуемые объекты удаляются.
- Бэкапы: `pg_dump` ежедневно, **шифруется age/gpg ДО отправки** → отдельный R2-бакет (lifecycle 30 дней; ПДн в открытом виде за пределы сервера не уходят, ADR-076 §7). Ключ шифрования бэкапов — вне сервера (офлайн-копия у владельца). R2 versioning на manifests-префиксе.
- Удаление аккаунта: soft-delete + анонимизация + GC-перезапись исторических манифестов (displayName → «deleted user») — ADR-076 §4/§7.

---

## 7. Клиент (SmartCon плагин)

### 7.1. Новые интерфейсы в `SmartCon.Core` (контракты + DTO, без Revit-вызовов — I-09)

```
Services/Interfaces/Cloud/
  ICloudCatalogApi          // тонкий контракт всех эндпоинтов §6.2 (DTO-in/DTO-out)
  ICloudAuthService         // login/refresh/logout, текущая сессия
  ICloudPublishService      // BuildDelta(localDb, baseSeq) → PublishResult|Conflict
  ICloudSyncService         // CheckUpdates / Pull(cloudDb) → SyncResult (применение манифеста)
  ICatalogManifestBuilder   // локальная БД → манифест v1 (pure, юнит-тестируемо)
  ICatalogManifestApplier   // манифест → запись в локальную копию (pure-план + executor)
  ICredentialStore          // Read/Write/Delete токенов (tech-stack §6.6)
Models/FamilyManager/Cloud/
  CloudCatalogConnection, CloudCatalogCard, PublishDelta, PublishConflict,
  CatalogManifest (и вложенные DTO), SyncPlan, SyncResult, AccessKeyInfo,
  SubscriptionInfo, CloudSession
```

### 7.2. Реализации в `SmartCon.FamilyManager/Services/Cloud/`

| Класс | Суть | Референс |
|---|---|---|
| `CloudCatalogApiClient` | HttpClient-обёртка: base URL из настроек, JWT-авторизация, refresh-on-401 (один повтор), retry ×3 transient, прогресс, net48 TLS/ReusePort хаки | `GitHubUpdateService` |
| `Win32CredentialStore` | Credential Manager (Meziantou 3.x), target `AGK.SmartCon.Cloud:{endpoint}:{accountId}` | tech-stack §6.6 |
| `CloudAuthService` | логин-диалог → токены в CredentialStore; refresh rotation; logout | ADR-076 §2 |
| `CatalogManifestBuilder` | читает локальную БД репозиториями → манифест; НЕ открывает Revit-документы (всё уже извлечено при импорте!); PII-whitelist (§5) | metadata package v4 |
| `CatalogManifestApplier` | план применения (insert/update/tombstone) → chunked commits по 10 (I-14), без WAL | Actualization Engine (ADR-054) |
| `CloudPublishService` | pull → build delta → prepare → upload missing → commit; конфликт-флоу ADR-077 §4 | — |
| `CloudSyncService` | check (`manifest/latest` ETag) → download manifest → diff → resolve+download missing → apply → lastSyncedPublishSeq | — |
| `CloudFileDownloader` | батч-resolve → параллельная (≤4) докачка presigned URL во временные файлы → verify SHA-256 → move в storage (read-only атрибут, I-16) | updater staging |

**Client-side snapshot для дельты (обязательный контракт C3):** рядом с
`catalog.db` опубликованной базы хранится `last_manifest.json.gz` — манифест
последнего успешного publish/pull. Дельта = `diff(BuildManifest(текущая БД),
last_manifest)` по `(itemId, versionLabel, contentHash, metadataHash)`;
`basePublishSeq` — из `CloudLink`. После успешного commit/применения —
`last_manifest` заменяется атомарно (tmp + move). Без этого файла
`ICloudPublishService.BuildDelta` нереализуем — change journal в локальной
схеме отсутствует и не добавляется.

**Контракт `CatalogManifestApplier` (identity preservation):** applier вставляет
items со **стабильными guid из манифеста** (item id автора = item id у всех
подписчиков) — от этого зависят: ES-маркер `SmartCon_FamilyVersion_v1` в
проектах подписчика (хранит CatalogItemId), ES-маркер `SmartCon_MiniProject_v1`
внутри staged `.rvt`, `family_dependencies.child_item_id`, стабильность diff.
**Version row id — локальные**: все FK внутри версии (`family_types.version_id`,
`extracted_attribute_values.version_id`, `family_dependencies.parent_version_id`)
ремаппятся applier'ом на локальные id. Таблица `family_nested_shared_families`
восстанавливается из секции `nestedSharedFamilies` манифеста (без открытия
файлов — I-01). `glb_state` применяется из манифеста.

DI: регистрация в `ServiceRegistrar` (синглтоны, как остальные сервисы модуля). Логирование: `BeginScope("Cloud", …)` по skill `smartcon-logging` (OpId сквозь publish/sync-цепочки; Warn с `[Action: …]`).

### 7.3. UI-флоу (канон от владельца 2026-08-25)

1. **Создание базы: диалог создания получает третий вид — «Облачная база» (решение владельца 2026-09-11: чистый диалог создания, БЕЗ радио-режимов; подключение по приглашению из создания убрано — оно живёт в «Подключить базу», см. ниже):**
   - **«Создать облачную базу»** = режим «пустая облачная база»: шаг аккаунта → имя → папка → серверный каталог + `CloudLink(Published)` **без публикации контента**: работа как с обычной локальной базой (импорт семейств по одному), публикация накопленного — командой «Опубликовать изменения» (§7.3.12). Режим дешевле для старта (не заливать гигабайты разом); полный путь «Опубликовать новую» (выложить текущее содержимое существующей базы) — post-MVP.
   - **«Подключить существующую» по приглашению = команда «Подключить базу» (решение владельца 2026-09-11):** диалог-развилка с радио — «Локальную базу из папки» (выбор папки) / «Облачную базу по строке приглашения» (вставка приглашения; формат ниже) → subscribe → первый pull. Локальный и облачный путь подключения не смешиваются с созданием. Ручной ввод (endpoint/slug/ключ отдельно) — fallback; endpoint по умолчанию = официальный сервер.
   - **Шаг аккаунта первым в обоих мастерах:** вход или регистрация (email+пароль, consent-чекбокс); без аккаунта дальше нельзя. Регистрация — прямо в мастере, не «где-то ещё».
   - **Формат приглашения:** одна строка `smartcon-cloud:subscribe:{base64url(JSON{endpoint?,slug,key?})}` — `key` включён только если автор выбрал «с ключом» при копировании; `endpoint` опущен для официального сервера. Кнопка **«Копировать приглашение»** — в меню инструментов базы (Published) и во вкладке «Доступ». Опечатка slug и «нет доступа» неотличимы (404-антиоракул, ADR-076 §4) — поэтому ручной ввод не рекомендован в UI.
   - **Модель связи:** `DatabaseConnection` получает новое **отдельное** свойство `CloudLink` (record: `Role: Published|Subscribed`, `Endpoint`, `CatalogId`, `Slug`, `LastSyncedPublishSeq`), миграция через `RegistryMigrator`. **`BaseType` (Kind) НЕ расширяется** — он ортогонален (General/Project = scope видимости/автоактивация, #119); облачная Published-база может быть и General, и Project-bound. Дубль-связка пишется и в `database_meta.remote_source_json` (обе роли) — self-heal `CloudLink` из `database_meta` при загрузке, при switch и **при повторном «Подключить базу»** (disconnect/connect — ссылка переживает переподключение, как Kind и project-binding; владелец 2026-09-11: «нет метаданных о том что база облачная»), и если registry.json был перезаписан старой версией плагина (даунгрейд молча стирает неизвестные поля RegistryDto). Forward-compat: старые версии читают registry.json с skip-unknown — база видна как обычная локальная.
2. **Настройки базы:** для локальной General/Project без `CloudLink` — команда **«Опубликовать базу»** (привязка к новому серверному каталогу, связка пишется в `CloudLink` + `database_meta`). Для облачной Published: **«Настройки»** (имя, описание, видимость, ключи доступа — список/создать/отозвать) и **«Снять с публикации»** (с подтверждением последствий для подписчиков). Для Subscribed: «Статус подписки», «Отключиться». **Реализовано в срезе (2026-09-11, владелец: «важная фича для вертикального среза»): «Снять с публикации» в меню инструментов базы → `DELETE /v1/catalogs/{slug}` → сервер удаляет публикации/подписки/неиспользуемые CAS-объекты и ставит tombstone; подписчик на ↻ получает 410 `catalog_unpublished`, уведомление строкой статуса, и **копия автоматически конвертируется в обычную локальную** (CloudLink очищается и в registry, и в `remote_source_json` копии — иначе self-heal воскресит связь; гейт записи снимается, значок перестаёт быть облачным — владелец 2026-09-11: «не смущать облаком»); локальная база автора остаётся и становится обычной (CloudLink очищается). Отличия от целевого E6: sunsetting-окно 30 дней с финальным pull — C3 (срез: tombstone до реюза slug, slug освобождается сразу). Удаление локальной Published-базы из списка авто-снимает публикацию с подтверждения (см. §13.21).
3. **«Обновить»** — это кнопка обновления дерева панели (↻, слева от поиска), НЕ «Обновить базу» в меню инструментов (актуализация БД — отдельная локальная операция, к облаку отношения не имеет; владелец 2026-09-11: «перепутали команды — pull должен жить на нашей кнопке обновления дерева»). **Всегда только pull, никогда push**: для активной `CloudLink.Role=Subscribed` — тихий check seq → при новой публикации pull с modeless-прогрессом → перезагрузка дерева; без изменений/сервера — просто дерево. Для `Published` — pull чужих публикаций мульти-автора (без отправки своих). Для локальных — текущее поведение без изменений. Push живёт **только** в отдельной явной команде «Опубликовать изменения» (§7.3.12). **Итог pull — дельта, не полный счётчик** (стресс-тест 2026-09-11: удаление одного семейства отображалось подписчику как «добавлено 8»): per-item дайджесты в sync-state.json → «добавлено X, обновлено Y, удалено Z» (нулевые части опускаются; 0/0/0 при новом seq = правки настроек каталога); первая синхронизация — всё «добавлено».
4. **Бейдж обновлений** на узле облачной базы (фоновый check раз в 15 мин + при показе панели). Локальный seq — только через `CloudSyncService.ReadLocalPublishSeq` (тот же сериализатор, что пишет `sync-state.json`): ad-hoc парсинг JSON в VM словил расхождение PascalCase/camelCase ключа и делал бейдж вечным при честном «актуально» у pull (ретест 2026-09-11).
5. **Конфликт публикации** — диалог ADR-077 §4 (три выбора + undelete-кейс, StatusDetailsView-паттерн; metadata-конфликт показывает число чужих изменений).
6. **Вкладка «Доступ»** в настройках облачной базы: ключи (создать → показать один раз + копировать; таблица: метка, активации, статус, отозвать), подписчики (счётчик; список email — Owner).
7. **Витрина маркетплейса (C5):** отдельное окно «Каталоги сообщества»: поиск, карточка (описание, счётчики, цена/ключ, **Revit-диапазон**), «Подключить».
8. **UI-гейтинг write-флоу на `Subscribed` базе (обязательный, C2):** композиция write-access — AND гейтов в `IDbAccessControlService` (RBAC, compat ADR-058, pending-actualization, `CloudLink.Role=Subscribed`); прямые `SetWriteAccess(true)` вне sync-сеанса запрещены (включая `InvalidateCache()` — ADR-075 §7). Гейтится (disabled + tooltip «Подписанная база доступна только для чтения»): batch/ручной импорт, переименование, удаление item/версий, `SetActiveVersion`, перезапись type catalog, теги, редактор категорий/атрибутов/правил, автоназначение, metadata package **импорт**, attribute library (From SP file/пресеты), assets-операции (добавить/удалить/primary/привязка, avatar crop), purge, RBAC-админка, конвертация General↔Project и привязка к проекту, «Опубликовать базу». UX — не падение SQLite посреди флоу. Проверка — централизованная, не ad-hoc if'ы в каждой VM. **Осознанное ограничение:** подписчик не может переключить активную версию (загружается `currentVersionLabel` автора); per-user override активной версии — post-MVP (открытый вопрос §13).
9. **Фидбэк публикации (Published):** sync/publish — modeless прогресс-диалог по паттерну ADR-048 (этапы: pull → подготовка дельты → upload X из Y → commit), пауза/отмена; итог — StatusNotice-отчёт «Опубликовано #43: 5 новых версий, 1 переименование, 42 МБ». Первая публикация большого каталога не блокирует панель; закрытие Revit посреди publish = пауза (идемпотентность E2: повторный запуск продолжит с prepare — недостающие файлы вычислятся заново). Ошибка квоты (422/413) — Warn с `[Action: освободите место или запросите увеличение квоты]`.
10. **Сессионные края:** истёкший refresh (не открывал Revit квартал) → бейдж «Требуется вход» на облачных базах + диалог логина по клику; подписки и локальные копии **не теряются** (токены перевыпускаются, связи в CloudLink/database_meta). Сервер недоступен при старте → бейдж «Нет связи» (отличный от «нет обновлений»!), контент работает. Фоновый poll: exponential backoff, обязательное уважение `Retry-After` при 429. TTL токенов — конфиг сервера + тестовые хуки (укороченные TTL для тестов refresh-rotation).
11. **«Мои подписки»** (окно аккаунта): после логина — список всех подписок аккаунта с сервера (`GET /v1/catalogs/subscriptions`); «Подключить заново» одним кликом без ключа (restore, E30) — сценарий новой машины/переустановки ОС.
12. **Семантика кнопок и команд по роли (решение владельца 2026-09-11):** «Обновить» на любой базе — только pull (см. §7.3.3). Публикация — отдельная явная команда **«Опубликовать изменения»** (ПКМ на облачной Published-базе + меню инструментов базы; иконка/тултип «опубликовать на сервер»): выполняет протокол pull-before-push из ADR-077 §2 — сначала подтянуть чужие публикации (мульти-автор), затем отправить свои локальные изменения (активные версии, §5); modeless-прогресс и итоговый отчёт — §7.3.9. Модель = Revit worksharing: доработал локально до стабильного → опубликовал. Автопубликация по расписанию/при импорте — отсутствует (PAT для ночной публикации — автоматизация вне UI, ADR-076 §2). Значки-облачка (финальный канон владельца 2026-09-11, стресс-тест): своя Published-база — **контурное облако без заливки** (`CloudOutline`, «просто облако» — как у проектной базы «просто папка»); подключённая по приглашению Subscribed — **контурное облако со стрелкой** (`CloudDownloadOutline`). Тултипы ролей различны. Реализационная грабля: `Kind` иконки по умолчанию обязан жить в `Style.Setter` — локальное значение на элементе перебивает `Style.Triggers` (приоритеты WPF dependency property), и стрелка у подписной базы никогда не показывается. Бейджи обновлений агрегируются per-база, без сводного счётчика. **Точка «есть локальные непубликованные изменения» (владелец, 2026-09-11: «не забывать публиковать после локальных изменений»)** — янтарная точка слева-сверху на шестерёнке инструментов базы, горит когда контент активной Published-базы отличается от последней публикации (`CloudPublishStateService`: дайджест манифеста без волатильных полей, файл `%APPDATA%\SmartCon\FamilyManager\cloud\published\{slug}.fingerprint`; «с этой машины не публиковали» — тоже горит, НО не на пустом каталоге — публиковать нечего, стресс-тест 2026-09-11); гаснет после publish, снимается при unpublish; пересчёт — после перезагрузки дерева (↻/смена базы/импорт). Дайджест чувствителен к ЛЮБОМУ изменению манифеста: новые семейства, новые версии, изменение контента активной версии (правки без инкремента — re-import overwrite current меняет sha файла), метаданные. Решение «что публиковать» — контентное, не по номеру версии: манифест несёт активные версии с sha256 файлов; публикация и точка смотрят на хэш, номер версии лишь организует историю (инкремент без правок тоже меняет манифест — новая version-строка — и уедет подписчику, файл не перекачается thanks CAS-дедупу).
13. **«Отключиться» (отписка):** подтверждение → подписка закрыта на сервере → диалог судьбы локальной копии: «Оставить замороженную» (default; контент доступен read-only) / «Удалить с диска» (purge с refcount-учётом общего CAS-кэша, E20).

### 7.4. Локальная копия подписчика

`%APPDATA%\SmartCon\FamilyManager\cloud\{catalogSlug}\` — обычная структура `catalog.db` + `files/`. `Mode=ReadOnly` всегда, кроме сеанса `CloudSyncService` (I-14); композиция write-access — единый authority в `IDbAccessControlService` (ADR-075 §7), после `SwitchToPath` принудительное ReadOnly применяется хуком подключения. **Database Actualization Engine на `Subscribed` копии отключён** (E21); **`LocalCatalogMigrator` работает в additive-режиме** (DDL-миграции схемы после апгрейда плагина — да; data-задачи — только через манифест; verify не считает DDL tampering). Гейт устаревших форматов — `hashFormatVersion`/`minPluginVersion` манифеста (E7) + hash-epoch `rehash` (ADR-077 §3c). **GLB-превью и avatar генерируются лениво в общий writable кэш** `cloud-cache\previews\{sha256}\` вне копии — первичная генерация группы превью показывает **modeless-прогресс по канону ADR-048** (этапы/отмена; одиночная генерация — существующий лёгкий прогресс вьювера); до генерации работает существующий fallback «превью на лету из managed `.rfa`» — 3D у подписчика доступен всегда, без ожидания (решение владельца 2026-09-11). `glb_state` применяется из манифеста («нет геометрии» не перегенерируется, #157). Поиск, дерево, загрузка в проект (loadable и system), stale detection, presence — по аудит-чеклисту C2 (§11): каждый существующий сервис, пишущий в БД/файлы, получает вердикт «работает / гейтится / отключено»; инвентаризация `CreateWritableConnection()` и auto-register `db_users` (на Subscribed отключён) — часть чеклиста. Своя RBAC в подписанной копии: все локальные пользователи — Engineer (read-only), `db_users` не синкается с сервером (модели разнесены, ADR-076 §1).

**Общий клиентский CAS-кэш** (`cloud-cache\sha256\`, E20): refcount по подпискам машины (purge одной подписки не удаляет объект, используемый другой); previews-кэш — LRU с лимитом (по умолчанию 2 ГБ).

---

## 8. Инфраструктура и деплой

### 8.0. Модель распространения: open source плагин + официальный hosted-сервер

**Плагин open source — сервер поднимать НЕ нужно.** Endpoint по умолчанию (официальный AGK Cloud) зашит в клиент; пользователь скачал плагин → зарегистрировал аккаунт на официальном сервере → публикует/подписывается. Никаких туннелей, доменов и аренды у конечного пользователя нет — инфраструктура целиком на стороне платформы. Аналогия: git open source, но репозитории хостит github.com.

- **Self-hosted сервер — только enterprise (C6)**, для компаний, которым запрещено хранить контент на чужой инфраструктуре. Тот же compose + SeaweedFS вместо R2.
- **Лицензирование (решение владельца 2026-08-26, §13.16):** один репозиторий, две лицензии — плагин MIT, `server/` под BSL 1.1 (запрет конкурирующего коммерческого хостинга, конвертация в Apache-2.0 через 36 мес). Enterprise self-hosted — платная лицензия поверх BSL (модель GitLab EE).
- **Изоляция серверов:** приглашение содержит endpoint (§7.3.1) — ключи/аккаунты/каталоги разных серверов не пересекаются и не конфликтуют; Credential Manager target включает endpoint (ADR-076 §2).
- **Масштаб официального сервера:** 100 авторов × квота 5 ГБ = 500 ГБ ≈ $7.50/мес в R2, egress $0. Монетизация платформы (помимо будущей комиссии маркетплейса): бесплатный тариф «1 каталог / 5 ГБ», расширение квот — платно (тарификация — отдельное ТЗ к C5).
- **Форк с чужим default endpoint** технически возможен (OSS) — принято: ценность в сетевом эффекте официального сервера (аккаунты, витрина, репутация), не в коде. **Clean-room риск записан явно:** облачный клиент — часть MIT-плагина, протокол открыт (манифест v1 + OpenAPI) → легальная независимая реализация сервера возможна; BSL покрывает только код `server/`, не протокол. Защита — сетевой эффект и темп разработки, принимаем сознательно.

### Фаза 0 — домашний ПК (бета, 0 ₽/мес + R2 free tier)

- Docker Desktop + `docker compose`: `api`, `postgres` (volume), `cloudflared` (named tunnel).
- Домен: **поддомен существующего домена владельца** в Cloudflare (named tunnel требует домен в аккаунте; quick-tunnels trycloudflare запрещены — случайный URL, лимит 200 conns). Endpoint фиксируется навсегда: `https://cloud.{домен}` — зашивается как default в клиент.
- R2: бакет `smartcon-cloud-cas` (+ `smartcon-cloud-backup`). Ключи R2 — в `appsettings.Production.json` вне репозитория / env.
- Через туннель идёт только API+JSON (условия Cloudflare о больших файлах не задеваем, ADR-075 §4).
- Windows autostart: compose `restart: unless-stopped` + Docker Desktop в автозагрузке. UPS/отключение света — accepted risk фазы 0 (бэкапы в R2 ежедневно).
- **Слой экспозиции — заменяемый, не часть продукта (вопрос владельца 2026-08-26):** `cloudflared` — опциональный сервис compose. Поддерживаемые варианты экспозиции одним и тем же сервером без изменений кода: (а) Cloudflare Tunnel (фаза 0, нет белого IP); (б) **белый IP + проброс 443 + Caddy** (авто-TLS Let's Encrypt) — сервер компании владельца или любой VPS; (в) self-hosted туннели (Pangolin/zrok/frp через свой VPS) или Tailscale Funnel; (г) **внутренняя сеть/VPN без выхода наружу** — дефолт для enterprise C6. Клиент узнаёт endpoint только из приглашения/настроек — ему безразличен способ экспозиции. Альтернативный compose-overlay с Caddy прикладывается в `deploy/` на C0.

### Переезд на VPS (когда появятся внешние пользователи)

Hetzner CX22 (~€4/мес): тот же compose, `pg_dump | pg_restore`, переключение туннеля на новый origin (cloudflared на VPS) или DNS на VPS + Caddy/nginx TLS. Endpoint не меняется, клиенты не замечают. Файлы в R2 не двигаются.

### Рост

R2 масштабируется сам (egress $0 — ключевая экономика, $15/мес за 1 ТБ vs ~$196 на S3). PG → managed при необходимости. API stateless → реплики за LB. Self-hosted enterprise (C6): тот же compose + **SeaweedFS** вместо R2 (S3-compatible, Apache 2.0; **MinIO запрещён** — CE мёртв: maintenance mode 2025-12, архив репозитория 2026-04).

---

## 9. Мульти-авторство — сводка (детали ADR-077)

Pull-before-push; дельта с `basePublishSeq`; per-item курсоры на сервере; 409 → pull → диалог (взять серверную / мою поверх с переназначением label / оставить обе); один автоматический retry без диалога, если после pull конфликтов нет; metadata-секция — целиком last-writer-wins с 409-гейтом; tombstones для удалений; Dependency Guard дублируется на сервере (422).

---

## 10. Реестр edge cases (проверять в тестах соответствующей фазы)

| # | Кейс | Ожидаемое поведение |
|---|---|---|
| E1 | Первый subscribe большого каталога, обрыв на 70% | Повторный pull докачивает только недостающие sha256 (файлы verify+move атомарны); lastSyncedPublishSeq не двигается до полного apply |
| E2 | Publish: залил файлы, commit упал (сеть) | Orphan-объекты в CAS → GC по окну; повторный publish идемпотентен (prepare скажет «missing пуст») |
| E3 | Два автора, разные семейства | Оба publish проходят (нет 409) — ADR-077 §3 |
| E4 | Два автора, одно семейство | Второй получает 409 → pull → диалог → переназначение label → publish |
| E5 | Отзыв ключа / suspend подписки во время активного sync | Текущая докачка завершается по живым presigned URL (accepted risk: окно 15 мин, ADR-076 §4); следующий resolve/manifest → `403 key_revoked`/`subscription_suspended` (свой, E36-текст); локальная копия остаётся рабочей |
| E28 | Ключ с `maxActivations=1` активирован дважды одновременно | Вторая активация отклонена сериализуемой транзакцией (422 «лимит активаций исчерпан»); честный покупатель просит у автора новый ключ или снятие лишней активации (suspend чужой — Owner) |
| E29 | Покупатель платного каталога на объекте без интернета 30+ дней | При subscribe текст «обновления требуют интернета раз в N дней, контент работает всегда»; Warn за 3 дня до истечения grace с `[Action: подключитесь к интернету]`; контент не блокируется никогда (E8) |
| E30 | Переустановка ОС / новая машина подписчика (Credential Manager стёрт) | Логин → окно «Мои подписки» → «Подключить заново» без ключа (subscribe идемпотентен для того же аккаунта — restore, активацию не потребляет); полный pull в пустую копию |
| E31 | Refresh-токен истёк (Revit не открывался квартал) | Бейдж «Требуется вход» + диалог логина; подписки, копии, CloudLink — не трогаются; после входа sync продолжается с `lastSyncedPublishSeq` |
| E32 | Сервер фазы 0 выключен (домашний ПК, свет отключили) | Бейдж «Нет связи» (≠ «нет обновлений»); весь контент работает; фоновый poll — exponential backoff; при появлении сервера — самовосстановление |
| E33 | Фоновый poll уперся в rate limit (429) | Уважение `Retry-After` + backoff; бейдж «Нет связи» НЕ показывается (это не outage); Warn в лог с `[Action: …]` при повторяющихся 429 |
| E34 | Автор переключил visibility/pricing при живых подписчиках | Таблица переходов §6.2: существующие подписки всегда grandfathered; диалог-подтверждение с числом затронутых подписчиков |
| E35 | Закрытие Revit посреди первой публикации 1 ГБ | Publish-диалог ставит паузу; повторный запуск — prepare вычисляет недостающие заново (идемпотентность E2), докачка продолжается; `last_manifest` обновляется только после commit |
| E36 | Подписчику отозвали доступ (suspend/revoke) | При следующем sync: понятный текст «Доступ к каталогу отозван. Локальная копия продолжает работать; обновления недоступны. [Action: обратитесь к автору каталога]»; копия НЕ удаляется |
| E6 | Каталог снят с публикации | **Sunsetting 30 дней:** `manifest/latest` → `{finalSeq, sunsetUntilUtc}` → подписчики делают финальный pull в окне (защита покупателя). После окна → 410 → **Subscribed**: копия «заморожена» (значок, sync отключён, контент доступен); **Published (свой каталог)**: конвертация в обычную локальную базу (CloudLink снимается, база остаётся read-write) |
| E7 | FHV-формат сменился (FHV23) у автора, подписчик на старом плагине | `minPluginVersion`/`hashFormatVersion` в манифесте → баннер «обновите SmartCon» (ADR-058), sync отклонён, копия read-only доступна |
| E8 | Подписчик офлайн 40 дней, платный каталог, check-in 30 | Sync заблокирован до онлайн-валидации; контент работает; Warn `[Action: подключитесь к интернету]` |
| E9 | Переезд сервера дом→VPS | Endpoint тот же (туннель/DNS) → клиенты не замечают; токены валидны |
| E10 | Переименование семейства автором | `normalized_name`/имя меняются в манифесте, content_hash (rename-invariant FHV) тот же → подписчик видит rename, не «новое семейство» |
| E11 | Автор удалил версию/семейство | Tombstone → подписчик помечает; файлы — purge по подтверждению; в проектах Revit ничего не трогаем. **Store-каталоги (C7): удаление item с живыми entitlements запрещено сервером** (422, §6.3.9) — покупатель никогда не теряет купленное через tombstone |
| E12 | Одинаковый `.rfa` в двух каталогах (CAS-дедуп) | Один объект в R2; `publish_point_files` ссылаются оба; GC учитывает все ссылки; resolve-проверка подписки per-catalog (ADR-076 §4) — знание хэша не даёт доступа |
| E13 | Корпоративный прокси режет `*.r2.cloudflarestorage.com` | Фолбэк через API: **download — C2** (`POST /v1/files/download`), **upload — C4** (`POST /v1/files/upload`); диагностика ошибки с `[Action: …]` |
| E14 | Конфликт по metadata-секции (категории правили оба) | 409 на metadata → «метаданные изменил X (категорий: N, атрибутов: M)» → pull → повторный publish (ADR-077 §7; data-loss window зафиксирован) |
| E15 | Подписчик модифицировал копию руками (обошёл read-only) | Следующий pull: verify = **SHA-256 файлов** против `file.sha256` манифеста + row-count spot-check (content_hash не используется для verify — его пересчёт требует Revit). Расхождение → «локальная копия повреждена, пересоздать?» (wipe + полный pull). **Actualization на копии отключён** (E21), DDL-миграции — additive и tampering'ом не считаются, поэтому ложных срабатываний нет |
| E16 | Refresh token украден и использован | Rotation-reuse детект → инвалидация цепочки сессии (ADR-076 §2) |
| E17 | Publish с машины с другим часовым поясом/сбитым временем | Все метки времени — серверные (`published_at_utc` выставляет сервер); клиентское время в манифесте игнорируется |
| E18 | Slug каталога занят / автор хочет сменить slug | Slug immutable после первой публикации (на него ссылаются подписчики); смена = новый каталог. UI предупреждает |
| E19 | `OverwriteCurrent` (ADR-040) на опубликованной версии | Сервер отклоняет same-label/новый-hash (422, ADR-077 §3b); клиентский publish-сервис заранее конвертирует такие изменения в новую версию (vN+1) — подписчики всегда видят иммутабельные опубликованные версии |
| E20 | Докачка и локальный CAS-кэш нескольких подписок на одной машине | Общий `%APPDATA%\…\cloud-cache\sha256\`: файл существует → не качаем, линкуем (копия в storage копии БД) |
| E21 | Actualization Engine на подписанной копии | Отключён полностью (ADR-075 §7); попытка «Обновить базу» на Subscribed → disabled + tooltip; гейт форматов — через `hashFormatVersion` манифеста (E7) |
| E22 | Два автора параллельно импортировали один и тот же `.rfa` (разные item_id, тот же контент) | Серверная дедуп-валидация publish (ADR-077 §3a) → второму 422 `duplicateOfItemId` → диалог «уже опубликовано {user}» → удалить локальный дубль / согласовать |
| E23 | Pull system family на подписанной копии | `fileKind=stagedRvt` → staged `.rvt` в storage копии → загрузка в проект через существующую isolation-project цепочку (ADR-061/062); обязательный интеграционный тест C2 |
| E24 | Подписчик на Revit 2021, каталог содержит файлы Revit 2025 | При subscribe — предупреждение по `revitVersionRange`; в дереве такие семейства помечены недоступными (бейдж), load команда объясняет причину |
| E25 | Подписчик имеет и свою локальную базу с тем же семейством | ES-маркер `SmartCon_FamilyVersion_v1` хранит только CatalogItemId и резолвится против **активной** БД (pre-existing поведение multi-DB, облако делает массовым): при переключении базы маркер другой базы — «осиротевший» (#218-обработчик). Item id глобально уникальны (guid автора), коллизий между базами нет. DB-scope в маркере — отдельный ADR, не C2 |
| E26 | FHV-миграция (FHV22→FHV23) у автора опубликованного каталога | Hash-epoch `rehash` publish point (ADR-077 §3c): item id сохраняются, файлы не меняются; подписчики на старом плагине — E7-гейт, остаются на предыдущем publish point |
| E27 | Даунгрейд плагина после подключения облачной базы | Старый плагин видит базу как локальную (skip-unknown); при записи registry.json теряет `cloudLink` → новая версия self-heal'ит CloudLink из `database_meta.remote_source_json` (обе роли пишут дубль-связку) |

---

## 11. Фазы и критерии приёмки

> Каждая фаза: build всех поддерживаемых конфигураций R19–R27 0/0 (клиент), `dotnet test` сервера, валидация `general`-субагентом, changelog-кандидат. Клиентские UI-фазы — ручной тест владельца + лог-валидация. Серверные фазы — Postman/HTTP-коллекция + xUnit (Testcontainers PG).

### C0. Спайк инфраструктуры (1–2 нед)

> **Статус (2026-09-11):** вертикальный срез (pre-C0) выполнен — `server/` (ASP.NET Core 10 minimal API: Identity-аккаунты, JWT+refresh rotation, каталоги, publish с транзакционным seq-гейтом, CAS со streaming SHA-256, manifest/latest, resolve/download) + `tools/SmartCon.Cloud.Probe` (полный позитивный/негативный сценарий, СРЕЗ ПРОЙДЕН). Отступления среза от плана зафиксированы в `server/README.md` (dev-CAS на ФС вместо R2 — `IObjectStorage` точка подмены). Оставшееся C0: R2-presigned round-trip из net48, туннель, замер манифеста реальной базы, прокси-тест.
>
> **Согласование полного среза v1 (владелец, 2026-09-11) — «супер-критичный функционал без украшений»:** два Revit против живого сервера (не файловая шара); в скоупе: «создать пустую облачную базу» (§7.3.1 режим б) + импорт по одному, «Опубликовать изменения» ПКМ (pull-before-push, только активные версии §5), приглашение + subscribe на второй машине, pull по «Обновить», загрузка loadable-семейства в проект из подписанной копии, read-only гейт Subscribed (центральный, минимум), значки-облачка, GLB лениво с прогрессом (fallback из `.rfa`). Вне скоупа v1: ключи/платность, вкладка «Доступ», Editors/конфликт-диалоги, витрина, rehash, store. Контрольная точка среза: «манифест реальной базы уезжает на сервер и возвращается».
>
> **Артефакты среза v1 (2026-09-11, реализация фаз 1–7):**
> - **Замер манифеста реальной базы владельца** («Библиотека семейств»: 24 активных семейства / 24 версии / ~166 CAS-объектов, 170 МБ на диске): манифест **4,10 МБ** compact / 5,95 МБ indented / **360 КБ gzip** — ~171 КБ/семейство compact, ~15 КБ gzip. Порог двухчастного манифеста (100 МБ несжатого, §5) не достигнут с запасом ×25 — **двухчастный манифест не нужен**, формат v1 одночастный.
> - **Probe против живого сервера** (127.0.0.1:8787, поднят 2026-09-11): полный позитивный/негативный сценарий — СРЕЗ ПРОЙДЕН (register → каталог → CAS upload ×3 + идемпотентность → publish #1/#2 → 409 stale_base_seq → 400 checksum_mismatch → subscribe/restore → manifest/latest → resolve → побайтовая загрузка SHA-256 → delta-download → 401 анонимно → refresh-ротация → reuse-детект E16).
> - **Статус реализации среза v1: КОД ЗАВЕРШЁН (2026-09-11, 16+ коммитов), ожидает финального ручного теста владельца на двух Revit + валидации smartcon.log.** Все gate-валидации фаз 1–6 — ПРИНЯТО; unit 3363/3363; integration R25 248/248; R19–R27 0/0; probe — СРЕЗ ПРОЙДЕН; validate-docs — PASSED (models/family-manager/cloud.md). Сервер: `docker compose up -d` в `server/deploy` поднимает postgres+api автономно (restart: unless-stopped, поднимаются с ПК; API-контейнер чинился — Dockerfile теперь удаляет server/global.json, пин SDK хоста несовместим с контейнерным). Dev-цикл `dotnet run` больше не нужен. Do-dont для следующего агента: pull живёт ТОЛЬКО на кнопке обновления дерева (§7.3.3), актуализация БД — отдельная локальная команда; карантин «Без категории» не публикуется (builder-фильтр + подтверждение автору).
> - Клиент (ветка feature/cloud-catalog): builder → applier (round-trip ≡) → auth/P-Invoke CredentialManager → publish/sync (CAS-кэш, атомарный swap, идемпотентность по seq) → CloudLink (registry v2 + V39 remote_source_json + self-heal E27) → read-only гейт Subscribed (ADR-075 §7) → UI-минимум §7.3 (мастер «Облачная база», логин, «Опубликовать изменения»/«Скопировать приглашение» в меню инструментов базы, «Обновить» = pull-only §7.3.3, бейдж обновлений, значки-облачка, GLB-кэш превью вне копии §7.4, RU/EN). Gate-валидации фаз 1–6 — ПРИНЯТО. Осталось среза: ручной тест владельца на двух Revit + лог-валидация; R2-presigned/tunnel/прокси — post-MVP C0-хвосты.

**Сделать:** compose (api «hello» + PG + cloudflared + uptime-kuma) на домашнем ПК; домен/поддомен в Cloudflare; R2 бакет; консольный probe-клиент (net48 и net8): login-stub → получить presigned GET → скачать файл; presigned PUT + `x-amz-checksum-sha256` → залить. **Проверить gotcha AWSSDK.S3 + R2: `AWSConfigsS3.UseSignatureVersion4=true`, `AmazonS3Config{ ServiceURL=…, SignatureVersion="v4", ForcePathStyle=true }`** (StackOverflow 2025-08, верифицировано 2026-08-25).
**Обязательные артефакты C0:**
1. Замер размера манифеста реальной базы владельца (несжатый/gzip, на семейство) — вход для решения о двухчастном манифесте (§5) до заморозки формата.
2. **Прокси-тест без лазеек:** probe-клиент за эмулированным блокирующим прокси (например, локальный Squid/whitelist без `*.r2.cloudflarestorage.com`) — доказательство, что E13-фолбэк нужен и работает через API. «Если доступен» — недопустимо.
3. Reference-структура `server/` (см. Приложение A) + рабочий `deploy/docker-compose.yml` (Приложение B) + таблица констант (Приложение C).
4. Стратегия DTO-контрактов: golden JSON-образцы манифеста в `server/contracts/manifest-v1.sample.json` (используются unit-тестами builder/applier клиента и серверными тестами); DTO пишутся вручную на обеих сторонах, codegen из OpenAPI отвергнут (net48 tooling).
5. Тестовые хуки TTL токенов (укороченные TTL в конфиге для тестов rotation).
**Приёмка:** файл скачивается по presigned URL из .NET Framework 4.8 процесса; то же за блокирующим прокси через API-фолбэк; compose поднимается одной командой; endpoint живёт снаружи по HTTPS; замер манифеста приложен к отчёту фазы.
**Выход:** `server/` скелет, `deploy/compose.yml`, ADR-075/076/077 → accepted, этот план → финализирован (включая решение по двухчастному манифесту).

### C1. Сервер MVP (4–6 нед)

Auth на **ASP.NET Core Identity** (register/login/refresh rotation/logout, throttling register/login, no email-enumeration, consent при register), catalogs CRUD (PATCH: `check_in_interval_days ≠ null` отклоняется feature-флагом до C4 — каталог не может жить без гейта незаметно), publish prepare/commit с валидациями §6.3 (включая дедуп, иммутабельность, rehash — ADR-077 §3a/§3b/§3c), CAS upload/download (**полный checksum-verify + magic-bytes, квоты §6.4**), manifest latest/{seq} (манифест отдаётся как хранимый `.json.gz` без повторного сжатия), files/resolve с per-catalog auth, access keys CRUD (дефолт maxActivations=1, аудит `access_key_events`), subscribe/unsubscribe (+consent, `subscription_events`; идемпотентный restore E30 — **на suspended-подписке subscribe НЕ снимает suspend**, ответ `403 subscription_suspended`), suspend/resume подписок, таблицы модерации (`reports`, `moderation_status`) и soft-delete аккаунтов — в схеме с C1. **Платформенные операции C1–C4 (квоты, verified, reset пароля, модерация) — только SQL/CLI владельца сервера; admin-роль в API не моделируется до C5.** Без marketplace-витрины; `check_in_interval_days` хранится, эндпоинт check-in — заглушка 200 (гейт платных активируется в C4). EF Core миграции в проде, drill восстановления из **зашифрованного** бэкапа (age-ключ офлайн), Uptime Kuma.
**Тесты:** xUnit + Testcontainers: auth rotation/reuse (E16), publish валидации (E2, E4-сервер, E22, иммутабельность, rehash), resolve auth (E12), ключи (revoke → subscribe 404-семантика, лимит активаций E28, идемпотентный restore E30), переходы visibility/pricing (E34). Postman-коллекция полного цикла — в репо (`server/tests/http/`).
**Приёмка:** полный цикл publish→manifest→download проходит из коллекции; OpenAPI сгенерирован; **backup drill по процедуре** (свежая БД с тестовым каталогом → pg_dump+age → R2 → снести контейнер и volume → восстановить → проверить: каталоги/подписки/ключи целы, ссылки manifest→CAS резолвятся; RTO цель — 30 мин); **Uptime Kuma мониторит api healthcheck И успешность backup-job** (алерт при пропуске бэкапа — бэкап без алертинга = нет бэкапа).

### C2. Клиент: подписка (8–10 нед) — закрывает S1 read-сторону

`ICredentialStore`/`Win32CredentialStore`, `CloudCatalogApiClient`, `CloudAuthService` (диалог логина), «Подключить существующую» в мастере создания БД + `CloudLink` на `DatabaseConnection` (RegistryMigrator, forward-compat, self-heal из `database_meta`), локальная копия, `CatalogManifestApplier` (identity preservation, FK remap, nested shared, glb_state — §7.2), `CloudSyncService`, «Обновить»-расширение, бейдж обновлений, Revit-version предупреждение при subscribe (E24).
**Отдельный подпункт — достижимость read-only (объёмная работа, выявлена ревью L2):** единый authority write-access (AND гейтов в `IDbAccessControlService`, ADR-075 §7), запрет `SetWriteAccess(true)` вне sync (`InvalidateCache`!), перевод read-запросов репозиториев с `CreateWritableConnection()` на read-коннекшены, отключение auto-register `db_users` на Subscribed, additive-режим `LocalCatalogMigrator`, полный UI-гейтинг (§7.3.8), GLB/avatar кэш вне копии (§7.4). **Аудит-чеклист:** каждый write-сервис модуля → вердикт «работает / гейтится / отключено», таблица публикуется в отчёте фазы. E1/E5/E6/E7/E10/E11/E15/E20/E21/E23/E24/E25/E26/E27.
**Тесты:** unit (builder/applier/diff/downloader с fake API + golden-образец манифеста из `server/contracts/`), integration (SmartCon.IntegrationTests: apply манифеста в реальную БД + открытие семейства из подписанной копии в Revit, **включая system family из staged .rvt**, E23; shared nested имена из `nestedSharedFamilies`, не placeholder, на R23/R24). Proxy-фолбэк скачивания (`/v1/files/download`, E13) — в C2: первый внешний корпоративный клиент появляется раньше C4 и почти гарантированно за прокси.
**Приёмка (измеримая):** (1) пошаговый тест-скрипт фазы (шаги → ожидание → строки лога) приложен к отчёту; (2) аудит-чеклист: 100% write-сервисов из baseline §2 имеют вердикт + ручную проверку (не «не падает», а список с галочками); (3) тест даунгрейда плагина R25→R24 с registry.json (self-heal CloudLink, E27); (4) прогон UI на EN-локали; (5) ручной тест: вторая машина подписывается по приглашению, качает, грузит loadable и system семейства в проект; обрыв сети на середине — докачка (E1); лог содержит все ключевые OpId-цепочки без необъяснимых Warn/Error.

### C3. Клиент: публикация (3–4 нед)

**Порядок фаз (обоснование):** publish (C3) **не зависит** от самой рискованной части C2 — read-only аудита и гейтинга: publish читает локальную rw-базу и шлёт на сервер. Поэтому разрешён трек **C3-lite сразу после C1** (один автор, без конфликт-флоу) — dogfood: владелец публикует реальный каталог и кормит C2 реальными данными. **Scope C3-lite явно включает auth-инфраструктуру** (`Win32CredentialStore`, `CloudCatalogApiClient`, `CloudAuthService`, диалог логина — переносятся из scope C2), `CatalogManifestBuilder` + `last_manifest`, `CloudPublishService` (без конфликт-диалога: один автор — конфликтов нет), без вкладки «Доступ» и витрины. Полный C3 (вкладка «Доступ», ключи, «Снять с публикации», OverwriteCurrent-конверсия) — после C2. Настоящий MVP первого внешнего корпоративного клиента = C0 + C1 + C2 (+C3-lite) — включая proxy-фолбэк скачивания (E13), иначе приёмка S1 на чужой корпоративной сети — лотерея.

«Опубликовать базу» в настройках локальной БД, мастер первой публикации (шаг аккаунта → что выкладывать: assets вкл/выкл — GLB всегда локальные), `CatalogManifestBuilder` + **`last_manifest.json.gz` snapshot** (§7.2), `CloudPublishService` (pull→delta→prepare→upload→commit; конверсия OverwriteCurrent → новая версия, ADR-077 §3b), modeless прогресс и отчёт публикации (§7.3.9), вкладка «Доступ» (ключи + «Копировать приглашение», §7.3.1), «Снять с публикации» (sunsetting E6). E2/E10/E17/E18/E19/E22/E35.
**Тесты:** unit (delta-builder, manifest round-trip: build→apply→build ≡), интеграция против живого сервера фазы 0. Ручной тест: опубликовать реальную базу владельца → подключиться со второй машины по приглашению.
**Приёмка:** S1 работает end-to-end одним владельцем; отчёт публикации показывает дельту и объём; прерывание/возобновление publish (E35) — по скрипту.

### C4. Мульти-автор + качество sync (4–5 нед)

Редакторы (invite по email), конфликт-диалог ADR-077 §4 (включая undelete-кейс и metadata-diff счётчики), retry без диалога, metadata-gate (E14), **активация check-in гейта для платных** (до C4 выбор интервала check-in скрыт в UI и отклоняется сервером — см. C1), фоновый poll (backoff, `Retry-After`, E32/E33), proxy-фолбэк upload (E13; download — уже в C2), GC-окна, rate limiting финал. E3/E4/E8/E14/E16/E29/E31/E36.
**Тесты:** интеграция «два клиента против одного сервера» (скрипт: оба правят → sync → assert). Ручной тест: владелец + второй аккаунт, роли Owner/Editor.
**Приёмка:** сценарий «радиатор + вентилятор» из требований владельца работает без конфликтов; конфликт на одном семействе разрешается диалогом; платный check-in гейтится по интервалу.

### C5. Маркетплейс (4–8 нед)

`discover`, публичные карточки (с Revit-диапазоном), витрина в плагине, статистика автору, веб-страница каталога (минимальная), сидирование 2–3 эталонными каталогами, «Пожаловаться». Платежи — вне платформы (ключи продаёт автор); встроенная оплата — отдельный epic после юр. проработки.
**Закладка под C7 (обязательна в C5, иначе C7 переделает витрину):** таблицы `credit_ledger`/`entitlements`/`item_prices`/`storage_profiles` в схеме с C5 (они уже в §6.1), preview-подписка как вид `CloudLink.Role`, гейтинг load-команд для запертых item. Логика баллов — C7, схема и data model — C5.
**До старта C5 — отдельный мини-ADR «Marketplace UI»**: витрина — единственный крупный UI без существующего паттерна-якоря в модуле (wizard «Облачная база», диалог одноразового показа ключа, conflict-resolution с edit-полем, бейджи «заморожен»/«check-in просрочен» — новые UI-паттерны C2–C4, оцениваются отдельно, а не «бесплатно» на StatusNotice; conflict-диалог — расширение StatusDetailsView, не новый диалог).
**Приёмка раздельная:** (а) **техническая** — витрина, карточки, подписка free/key, статистика работают (контролируется исполнителем); (б) **запуск** — юр. пакет (оферта, EULA каталога, ФЗ-152, политика модерации) готов у владельца + первый внешний автор публикует платный каталог и внешний покупатель подключается по ключу (НЕ контролируется исполнителем).

### C6. Enterprise (по запросу)

Self-hosted compose + SeaweedFS, OIDC/SSO, организации/пространства, audit log UI, **миграция каталогов между серверами (export/import с перепривязкой подписчиков)** — отдельное ТЗ при первом enterprise-клиенте.

**BYOS — Bring Your Own Storage (вопрос владельца 2026-08-26, включаем в enterprise-тариф):** корпоративный клиент хранит файлы семейств в СВОЁМ S3-совместимом облаке (Яндекс Cloud Object Storage / Selectel / VK Cloud / on-prem SeaweedFS), наш сервер хранит только метаданные. Архитектура готова с рождения: метаданные и CAS разделены (ADR-075 §4), S3-клиент сервера работает с любым совместимым endpoint'ом — меняется только конфиг. Три уровня:
1. **BYOS:** в настройках каталога — свой storage-профиль (endpoint/bucket/ключи, ключи шифруются на сервере); presigned URL генерирует наш сервер на ИХ бакет; байты файлов через нас не проходят никогда.
2. **Storage-gateway:** компания поднимает у себя мини-контейнер-шлюз (в нашем compose), который сам генерирует presigned URL — мы не получаем даже ключи от их бакета; `/files/resolve` редиректит клиента на их шлюз.
3. **Полный self-hosted** — как C6 выше.

Схема: `storage_profiles(id, kind: builtin_r2|external_s3|gateway, config_json/secret_ref)` + `catalogs.storage_profile_id` (default = builtin R2). Тарификация: BYOS/gateway — платный корпоративный уровень (рельс 1, C8). Egress их облака — их счёт. Миграция каталога между профилями хранения — переливка объектов + переключение профиля (CAS-хэши неизменны, переливка идемпотентна).

**Правила BYOS (закрывают стыки, найденные ревью):**
- **CAS per профиль:** `cas_objects` PK = `(sha256, storage_profile_id)` — дедуп только внутри профиля (кросс-профильный дедуп запрещён: иначе resolve выдавал бы наш R2 за наш счёт для чужого каталога или наоборот). Один и тот же контент в двух профилях = два объекта — осознанная цена изоляции.
- **GC на чужом хранилище:** external_s3 — наш GC их ключами с delete-scope IAM (или lifecycle-правила на их стороне — выбор клиента); gateway — объекты полностью их зона ответственности, мы лишь выдаём orphan-report (список sha256 без ссылок).
- **Аналитика и баллы:** gateway обязан репортить download-события обратно (batch API `POST /v1/files/gateway-events`), иначе статистика автора и начисление баллов C7 не работают → **gateway-профиль несовместим со store-каталогами** (ограничение тарифа, фиксируется в оферте).
- **Квоты:** per-user storage quota действует только на builtin R2; на BYOS-профилях объём контролирует владелец хранилища.

### C7. Магазин семейств на баллах (направление, владелец 2026-08-26) — ПОСЛЕ C5

Эволюция маркетплейса в магазин внутри Revit. **Экономика — бартерные баллы (РЕШЕНО, владелец 2026-08-26):** санкционные ограничения (владелец — РБ, основная аудитория — РФ) делают денежные выплаты авторам юридически тяжёлыми; балльная система требует нулевой платёжной инфраструктуры и решает холодный старт витрины («чтобы скачивать — публикуй»).

- **Начисление баллов — не за факт публикации, а за качество** (антимусор — наше уникальное преимущество, ADR-059): баллы за семейство, которое (а) прошло Import Validation Gate, (б) уникально по FHV-хэшу по всей платформе (CAS-дедуп: дубль/чужое = 0 баллов), (в) начисление частями: при публикации + при первых N скачиваниях **другими** пользователями (антинакрутка: stats = distinct users, §6.1). Баллы за семейство замораживаются на 90 дней и сгорают, если оно удалено или снято с публикации в этот период (анти «выложил мусор — потратил — удалил»).
- **Трата баллов:** скачивание семейства поштучно (цена в баллах от автора/платформы) или пак целиком (оптовая скидка в баллах). Бесплатные семейства/паки — выбор автора (`pricing='free'` остаётся).
- **Модель доступа (из решений 2026-08-26):** поштучно купленное за баллы семейство — доступ пожизненно + все его обновления; пак — на год («всё текущее + новые семейства пака в течение года»), потом скачанное остаётся, новое — за продление. Entitlements: `{user_id, catalog_id, item_id NULL, kind: lifetime|annual, updates_until_utc NULL, paid_credits}`.
- **Механика доступа (критично, решается здесь, не «по ходу C7»):** `/files/resolve` для store-каталогов — **entitlement-aware per-item** (не subscription-aware): файл item отдаётся, если есть lifetime entitlement на item ИЛИ annual entitlement на пак с `updates_until_utc >= published_at_utc` версии (семейства, добавленные после истечения годового окна, заперты). Манифест применяется целиком (все семейства видны — витрина), файлы запертых item отсутствуют **by design** — E15-verify для store/preview-копий проверяет только файлы разблокированных item (отсутствие запертого файла ≠ повреждение). Запертые item в дереве — значок замка; load-команды (LoadToProject/Place/DnD) для них гейтятся с оффером покупки.
- **Preview-подписка («витрина без файлов»):** `CloudLink.Role` получает третье значение `Preview` (RegistryMigrator + self-heal, как у остальных ролей). **Поштучная покупка автоматически создаёт preview-подписку** на пак-каталог (купил два семейства из чужого пака → пак появился в списке баз, разблокированы купленные). Покупатель крутит 3D, читает параметры всех типов, смотрит фото — но загрузить в проект не может до покупки.
- **GLB для store-каталогов публикуются** (точечное исключение из ADR-075: иначе 3D до покупки невозможно). Опция «публиковать 3D-превью» в настройках публикации.
- **Представление в панели (РЕШЕНО):** каждый купленный/подключённый пак = отдельная облачная база в списке + настройка видимости (как общие/проектные базы). Команда **«Мои покупки»**: авторы → пакеты, статус (активна/истекает/истекла), видимость per пак, «Продлить». Внутри пака купленное поштучно разблокировано навсегда, остальное — preview.
- **Окно магазина:** глобальный поиск по семействам всех авторов (серверный `pg_trgm`, §6.1), карточка (фото/3D/параметры/цена в баллах), корзина, «Купить и загрузить в проект» — без выхода из Revit. Страницы авторов с рейтингом.

### C8. Монетизация платформы (направление) — рельсы по мере готовности

Деньги в систему входят отдельными независимыми рельсами (каждый — своё юридическое ТЗ; порядок = от простого к сложному):

1. **Рельс 1 — корпоративные тарифы облака (первый доход, раньше магазина!):** платные квоты/каталоги для S1-компаний на hosted AGK Cloud. Оплата — **безналичные счета для юрлиц** (РФ/РБ стандарт; НЕ нужны MoR/карты/эквайринг). Покрывает инфраструктуру с первых клиентов.
2. **Рельс 2 — продажа пакетов баллов физлицам:** локальный эквайринг (ЮKassa/самозанятый/ИП для РФ; РБ-агрегаторы). Баллы нельзя вывести в деньги → цифровой товар, а не электронные деньги (юридически легко).
3. **Рельс 3 — реальные деньги авторам (мечта, C9+):** премиум-паки за $/₽ для verified-авторов с выплатами. Варианты: крипто-выплаты, агентская модель с самозанятыми, локальный агрегатор. **Отдельное юридическое исследование, когда объёмы магазина его оправдают.** Риск привыкания сообщества к «всё за баллы» — митигация: премиум-трек только для нового/эксклюзивного контента, балльная экономика существующего не отзывается.
- **Защита покупателя (любой рельс):** купленное остаётся навсегда: entitlement не отзывается при unpublish; **GC-исключение считается через join entitlements → item_versions → sha256** (не через publish_point_files — они удаляются после sunsetting); удаление проданного item запрещено сервером (§6.3.9). **Семантика «навсегда» честная:** навсегда — это локальная копия покупателя + файлы в хранилище; **restore на новой машине после полного удаления каталога (после sunsetting-окна) невозможен** — «Мои покупки»/restore работают только для живых каталогов (рекомендация покупателю: копия = его актив).

> **Накладные расходы (заложить в каждую фазу):** сборка ×4 конфигурации, валидация `general`-субагентом, ручной тест владельца + лог-аудит, changelog-кандидат — суммарно ~0.5–1 нед на фазу, в оценки выше включены.

---

## 12. Требования к реализации (жёстко)

1. **Инварианты I-01…I-17** без исключений. Особо: I-09 (Core — только контракты/DTO; HTTP и Credential Manager — вне Core), I-14 (копия подписчика — DELETE journal, `LocalCatalogDatabase`, read-only вне sync), I-16 (скачанные файлы — read-only атрибут, verify SHA-256), I-10 (MVVM), I-01 (sync не трогает Revit API — документы не открываются при publish/sync; единственное исключение C4-опция: ленивая генерация GLB — через существующий `IFamilyManagerAwaitableEvent`).
2. **Multi-version:** клиент собирается R19–R27 (2019–2024 net48, 2025–2026 net8, 2027 net10; `SmartCon.Tests` — net8, под R27 не собирать — AGENTS.md); новые NuGet в клиент — только после проверки net48-совместимости и ILRepack/ALC (ADR-051): Meziantou CredentialManager — CPM-пин в `src/Directory.Packages.props` + строка в **оба** ItemGroup `SmartCon.Dependencies.csproj` (net48 merge — Meziantou strong-named, re-sign нашим snk допустим по правилу csproj; net8 — после проверки ALC-изоляции). `ServicePointManager.ReusePort` поднять в startup (`App.cs`, рядом с SecurityProtocol) — `CloudCatalogApiClient` может инициализироваться раньше `GitHubUpdateService`. Форматные спецификаторы в interpolated strings — запрет #97 (файлы с HelixToolkit не трогаем, но правило помним).
3. **Логирование:** skill `smartcon-logging`: `BeginScope("Cloud", …)`, OpId-цепочки publish/sync, Warn + `[Action: …]`, `Path.GetFileName()` в scope, без внешних scope на долгих операциях (C15: sync-цикл — точки логирования на этапах, не один scope на весь pull).
4. **Тесты:** skill `smartcon-testing`. Unit — diff/build/apply/conflict (pure). Integration — через SmartCon.IntegrationTests для Revit-boundary частей (apply → открыть семейство в Revit). Сервер — xUnit + Testcontainers. 9 жёстких правил интеграционных тестов соблюдать.
5. **Документация:** новые доменные модели → `docs/domain/models/family-manager/cloud.md`; интерфейсы → `docs/domain/interfaces/family-manager/cloud.md`; глоссарий §2 → `docs/domain/glossary.md`; обновить `docs/family-manager/README.md` (провайдеры, статус фаз), `docs/README.md` (статусы), этот план — статусы фаз.
6. **Локализация:** все строки UI — через LanguageManager/`LocExtension` (ADR-020), RU/EN.
7. **Запреты:** S3 SDK в клиенте; plaintext токены; `new SqliteConnection` вне `LocalCatalogDatabase`; WAL; MinIO; quick-tunnels; real-time sync (WebSocket) в MVP; EF Core в клиенте; хранение `.rfa` нескольких Revit-версий на одну версию семейства (legacy-read only).
8. **Миграции локальной схемы:** `database_meta.remote_source_json` — по существующему паттерну `LocalCatalogMigrator` (V39+; текущая схема main — V38), с actualization-задачей если нужен backfill (skill `smartcon-db-actualization`). `CloudLink` в `registry.json` — через `RegistryMigrator` (отдельный механизм, НЕ путать с мигратором catalog.db).
9. **Performance-бюджеты (проверяются в ручных тестах C2/C3):** фоновый check обновлений — < 2 c и < 10 КБ на базу; **десятки облачных баз (магазин C7): poll батчится одним запросом** (`GET /v1/catalogs/subscriptions` возвращает hasUpdates по всем) — не N запросов за цикл; sync 5 изменённых семейств — < 1 мин на 10 МБит/с; первый pull 1 ГБ — фоновый, без блокировки панели, с прогрессом и докачкой; открытие панели с cloud-базой — без сетевых вызовов на UI-потоке (всё через кэш/фон); publish 100 семейств — modeless, без блокировки Revit. Домашний сервер фазы 0: 20 подписчиков × poll 4/час — нагрузка пренебрежима (JSON-килобайты).

---

## 13. Открытые вопросы (решать по ходу, не блокеры C0–C3)

1. Восстановление пароля без веб-кабинета (C5) — на C1–C4: ручной reset владельцем сервера.
2. Лимиты на автора (кол-во каталогов/ГБ) на фазе 0 — мягкие, мониторинг вручную.
3. Юридический пакет C5 (оферта, EULA каталога, ФЗ-152, политика модерации) — владелец с юристом; техника готова раньше.
4. Перенос ownership каталога между аккаунтами — C4+.
5. Постраничный/двухчастный манифест — решение по замеру C0 (§5).
6. **Форк** (импорт подписанного каталога в новую editable базу): легален для free-каталогов, для key-каталогов — вопрос EULA; технически — «Экспорт в новую локальную базу», решение до C5.
7. **Subscriber → Editor** (Owner добавил подписчика в редакторы): конвертация копии в published-capable или пересоздание — решение до C4.
8. **Re-publish после «Снять с публикации»**: реанимация того же slug (подписчики «оживают») vs новый каталог — решение до C3; склоняемся к реанимации с явным подтверждением.
9. **Жизненный цикл аккаунта**: схема заложена в C1 (soft-delete, consent, анонимизация — ADR-076 §7); **UI/процедуры** удаления аккаунта и смены email/пароля — C5 (веб-кабинет).
10. **Точечная публикация (владелец, 2026-09-11):** публикация отдельного семейства через ПКМ (не всей базы) — «Опубликовать семейство» в контекстном меню дерева; под капотом тот же pull-before-push с полной сборкой манифеста (манифест всегда полный срез активных версий), но с подтверждением охвата. Пост-MVP (после ручного теста среза v1).
10. **Перенос каталогов между серверами** (AGK Cloud ↔ self-hosted C6): export/import с перепривязкой подписчиков — ТЗ при первом enterprise-клиенте.
11. ES-маркер без DB-scope (E25): массовость сценария растёт с облаком — отдельный ADR при необходимости, не C2.
12. `subscriptions.expires_at_utc` (временные подписки, «аренда каталога на год») — post-MVP; семантика `expiresAtUtc` ключа = дедлайн активации, не путать (ADR-076 §3).
13. **Per-user override активной версии у подписчика** (загрузить не `currentVersionLabel`, а старую v2): ограничение зафиксировано (§7.3.8), override живёт вне catalog.db (user-settings) — post-MVP, если попросят.
14. Точка входа в витрину маркетплейса (меню/вкладка), UX пустой витрины, внешняя discoverability — в мини-ADR «Marketplace UI» до C5. Маркетинг/привлечение авторов — non-goal этого плана.
15. **Retention-политика версий на каталоге** («хранить последние N версий» / «не старше года»): идея владельца 2026-08-26. Уточнение 2026-09-11 (см. §5): **по умолчанию облако хранит все опубликованные активные версии** (активные срезы всех живых publish points — «облачные резервные версии» в духе Revit server; мульти-авторские публикации поверх чужих копятся там же), а локальные промежуточные версии автора не публикуются вовсе — это базовая экономия R2, стоимость хранения ничтожна ($0.015/ГБ/мес, egress $0). Полная выкладка локальной истории версий item'а — post-MVP опция автора; авто-подчистка старых publish points — опция автора, post-MVP. Серверная механика уже есть (delete-version → tombstone → GC), нужен только планировщик и UI-настройка.
16. ~~Открытость кода сервера~~ **РЕШЕНО (владелец, 2026-08-26):** репозиторий НЕ разделяем. Один публичный репозиторий `AGK-SmartCon-Pro`: плагин остаётся под **MIT** (корень, существующий LICENSE не трогаем), папка **`server/` — под BSL 1.1** (свой `server/LICENSE`: запрет конкурирующего коммерческого хостинга, конвертация в Apache-2.0 через 36 месяцев с даты релиза; модель Sentry/HashiCorp; mono-repo с двумя лицензиями — модель GitLab `ee/`). Обязательная маркировка: `server/README.md` с явным блоком лицензии, шапка в каждом файле сервера, корневой README получает абзац «Лицензии: плагин — MIT, server/ — BSL». Форк плагина без сервера = плагин без облака; clean-room сервер по открытому протоколу — принятый риск (§8.0).
17. **Endpoint/domain migration** (ревью L6): смена домена сервера осиротевает CloudLink/приглашения/Credential Manager targets. Минимум: серверный `308` с новым endpoint → клиент обновляет CloudLink и re-login. Точная механика — отдельный ADR, когда появится спрос (переезд домена платформы маловероятен до C5).
18. **Known limitation (ревью L6):** файл >100 МБ для клиента за прокси, блокирующим R2, недоступен ни напрямую, ни через API-фолбэк (лимит free-прокси туннеля, §14) — диагностика с `[Action: разрешите *.r2.cloudflarestorage.com]`. На VPS с публичным IP лимит снимается (фолбэк не через Cloudflare-прокси).
19. **Перенос v2.1.0-данных через манифест (актуализация 2026-09-11, решение до C2):** план писался до World B и ADR-071. Три группы данных теперь живут в каталоге, но не описаны в манифесте v1 явно: (а) **routing World B** — `item_routing_rules`/`item_routing_type_settings` (V37), `family_segment_rules` (V38), `family_segment_sizes` (V36): без них у подписчика пустая вкладка «Трассировка» и деградирует загрузка системных семейств; (б) **per-type хэши** `family_type_hashes` (V32) и **section-хэши** `section_hashes/section_strings` (V33): на Subscribed-копии actualization отключена (ADR-075 §7) → бэкфилл-задачи (`type-hashes-v1`, `section-hashes-v1`) не могут наполнить эти колонки, единственный источник — манифест (иначе per-type stale-карта и diff-окно на подписанных копиях неработоспособны); (в) **правила автоназначения** — уже в metadata v4 (`assignmentRules`, #241), мета-слой манифеста переносит их автоматически. Кандидат-решение: секции manifest v1 для (а) и (б) (данные уже лежат в БД автора, сериализация по образцу V33-JSON); включение в golden-образец `server/contracts/manifest-v1.sample.json` — обязательно.
20. **Публикация проектных баз (Kind=Project) и правила активации (владелец, 2026-09-11, стресс-тест):** `CloudLink` ортогонален `BaseType`, но UX публикации Project-базы не решён: удалённая команда работает над проектом (или несколькими) — как подписная копия соотносится с project binding (шаблон имени файла, автоактивация, #119)? Варианты: (а) подписная копия всегда General (правила активации не переносятся — контент без автоактивации); (б) binding переносится и копия сама становится проектной у подписчика; (в) перенос с перепривязкой под проект подписчика. Синхронизируются ли изменения binding'а при повторных publish — отдельный вопрос (манифест v1 его не несёт). Решение до фазы мульти-автора/C1; для среза v1 публикуем только General.
21. **Удаление локальной Published-базы = orphan-каталог на сервере (владелец, 2026-09-11) — МИНИМУМ РЕАЛИЗОВАН в срезе:** `DeleteDatabaseAsync` сам по себе по-прежнему не смотрит на `CloudLink`, но VM-флоу удаления базы теперь при `Role=Published` показывает подтверждение («база будет автоматически снята с публикации — подписчики потеряют обновления») и делает `DELETE /v1/catalogs/{slug}` ДО локального удаления; при недоступности сервера — второе подтверждение «каталог останется на сервере, всё равно удалить?». Явная команда «Снять с публикации» (§7.3.2) тоже в срезе. Осталось на C3: sunsetting-окно 30 дней с финальным pull (E6), восстановление orphan-каталогов перепривязкой по slug (например, после «удалить всё равно» при лежавшем сервере), отписка подписчика при удалении его локальной копии (сейчас подписка на сервере живёт — безвредно, чистится при unpublish каталога).
22. **Производительность точки «непубликованные изменения» (ревью gate-валидации 2026-09-11, P3):** пересчёт дайджеста запускается на каждом обновлении дерева и пересобирает манифест с SHA-256 всех файлов каталога — на больших каталогах это секунды дискового IO на каждый ↻. Post-MVP оптимизация: in-flight guard (не запускать параллельный пересчёт) + кэш хэшей по (путь, mtime, size) в fingerprint-файле. Гонка «переключение активной базы между guard и build» даёт врущую точку до следующего ↻ — само-исцеляется, тот же класс гонок, что RefreshDatabaseUpdateStateAsync (ADR-058 #173).

---

## 14. Верификация технологий (проверено 2026-08-25, источники)

| Решение | Факт | Источник |
|---|---|---|
| Сервер на .NET 10 | LTS, поддержка до 2028-11; .NET 8 LTS истекает 2026-11 — новые серверные проекты на нём не начинаем | dotnet.microsoft.com support policy (2026-08) |
| PostgreSQL 18 | Current major (18.6, 2026-08-13); PG 19 в beta | postgresql.org release notes |
| Npgsql 10 / EF Core 10 | Релиз v10.0.0, таргет .NET 10, CI на PG 18 | github.com/npgsql release v10.0.0 |
| **MinIO запрещён** | CE: фичи вырезаны 2025-05, бинарники/Docker остановлены 2025-10, maintenance mode 2025-12, репозиторий заархивирован 2026-02/04 | glukhov.org, itsfoss.com (2026-04) |
| Self-hosted S3 = SeaweedFS | Apache 2.0, production-ready с 2015, drop-in по S3 API (C6) | сравнения 2026 (elest.io, lowcloud.io) |
| Cloudflare R2 | egress $0; free tier 10 ГБ; S3-compatible; presigned GET/PUT поддержаны | developers.cloudflare.com/r2 (2026-04) |
| AWSSDK.S3 + R2 presigned | Работает; обязательны `UseSignatureVersion4=true`, `SignatureVersion="v4"`, обычно `ForcePathStyle=true` | Cloudflare docs + SO #79296886 (2025-08) |
| Cloudflare Tunnel | Named tunnel требует домен в аккаунте Cloudflare; quick tunnels (trycloudflare) — только тесты (200 conns, случайный URL); free CDN ToS ограничивает «непропорциональную раздачу больших файлов» → файлы только через R2. **Лимиты free-туннеля (подтверждено опытом владельца + сообществом): 100 МБ на тело HTTP-запроса, буферизация тела проксей (ECONNRESET на больших upload), занижение скорости на дальних маршрутах** → туннель ТОЛЬКО для API/JSON (манифест с gzip — единицы МБ, вписываемся); в `cloudflared` конфиге `chunked_encoding = true` + увеличенные timeouts | developers.cloudflare.com/tunnel (2026), community.cloudflare.com, r/selfhosted (2026), опыт владельца 2026 |
| Cloudflare R2 цены | **Egress бесплатно официально** (док. обновлена 2026-08-07: «no charges for egress bandwidth for any storage class», включая S3 API); хранение $0.015/ГБ-мес, 10 ГБ free; операции A $4.50/млн (1 млн free), B $0.36/млн (10 млн free). Presigned URL → клиент идёт на `r2.cloudflarestorage.com` = продукт R2 (egress free), НЕ бесплатный CDN-прокси (ToS-ограничения на него не распространяются) | developers.cloudflare.com/r2/pricing (2026-08-07) |
| Meziantou CredentialManager | Линия 3.x актуальна; Win32 CredRead/CredWrite; net48+net8 совместимо | nuget.org / github.com/meziantou |
| Ключи доступа (модель) | Индустриальный паттерн: activate → токен → periodic validation → offline grace → revoke прекращает обновления | Keygen docs, Lemon Squeezy license API, Gumroad licenses |
| Конкурентная рамка | Kinship Collections (internal/external share, point-in-time copy при unshare), UNIFI/Content Catalog (Revit-version insert), BIMobject (производители платят за публикацию) — наша дифференциация: публикация в 1 клик из локальной базы + валидация качества | kinship.io docs/blog, unifilabs.com, bimobject.com |
| Delta-sync паттерны | casync/OSTree/bsdiff-чаны отложены: `.rfa` маленькие, CAS per-file достаточен | systemd/casync, ostree docs, koder RFC-001 |

### Повторная верификация (2026-09-11, после rebase ветки на v2.1.0)

| Решение | Подтверждение |
|---|---|
| .NET 10 LTS | Поддержка до 2028-11-14 (dotnet.microsoft.com, текущий патч 10.0.11 от 2026-08-11); .NET 8 истекает 2026-11-10 — решение «не начинать на .NET 8» верное |
| MinIO запрещён | Подтверждено: репозиторий ARCHIVED (финально 2026-04), binary/Docker source-only с 2025-10; SeaweedFS (Apache 2.0) — валидная замена для C6 |
| Cloudflare R2 | Egress $0 подтверждён (docs 2026-08 + community); presigned GET/PUT через AWSSDK.S3 работают, SigV4-gotcha актуален (`UseSignatureVersion4=true`, `SignatureVersion="v4"`, `ForcePathStyle=true`; для серверных streaming-операций — `DisablePayloadSigning`/`DisableDefaultChecksumValidation`) |
| Cloudflare Tunnel | Лимит 100 МБ — только на **upload**-тело (Free/Pro; Business 200 МБ, Enterprise 500 МБ); на download лимита нет — ограничение затрагивает только upload-фолбэк C4, download-фолбэк C2 шире, чем считал план |
| Npgsql / EF Core | Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 (2026-07), таргет .NET 10, поддержка PG18 (virtual generated columns, uuidv7) |
| Meziantou CredentialManager | Линия 3.0.x жива (3.0.1); net462+net8+net10 |

Источники: dotnet.microsoft.com/platform/support/policy, github.com/minio/minio (README archived), glukhov.org (MinIO timeline), developers.cloudflare.com/r2 (presigned-urls, aws-sdk-net), community.cloudflare.com (upload limits, 100mb tunnel limit), nuget.org (Npgsql EFCore.PG, Meziantou), postgresql.org (PG18 release).

---

## 15. Риски и митигация

| Риск | Вероятность / ущерб | Митигация |
|---|---|---|
| Сервер — новая ops-область (бэкапы, секьюрити, аптайм домашнего ПК) | Высокая / средний | Docker-first, ежедневные pg_dump в R2, фаза 0 без SLA, мониторинг uptime (Uptime Kuma на том же ПК) |
| Пустой маркетплейс на старте | Высокая / высокий для C5 | S1 первым (ценность без маркетплейса), сидирование 2–3 каталогами |
| Юридика платежей/модерации | Средняя / высокий | Ключи продаются вне платформы (C1–C4 юридически чисто); платежи — после юр. ТЗ |
| Корпоративные прокси ломают R2/tunnel | Средняя / средний | Обязательный прокси-тест в C0 (без «если доступен»); download-фолбэк через API в C2, upload — C4; диагностика с Action-подсказками |
| Регрессии локального FamilyManager | Средняя / высокий | Облако — отдельный слой; локальный код не меняется кроме точек расширения (CloudLink, «Обновить», гейтинг); полный регресс-сьют каждую фазу; аудит-чеклист C2 |
| Утечка ключей/токенов | Низкая / высокий | Хэши ключей, Credential Manager, refresh rotation, rate limits, audit log |
| Раздувание scope (real-time, SSO, платежи) | Средняя / высокий | Non-goals §1 зафиксированы; отклонённые альтернативы в ADR |

---

## Приложение A. Reference-структура `server/` (артефакт C0)

```
server/
├── SmartCon.Cloud.sln
├── global.json                          # pin .NET SDK 10.x
├── Directory.Packages.props             # CPM сервера (отдельный от src/)
├── src/
│   ├── SmartCon.Cloud.Api/              # ASP.NET Core 10: controllers/minimal API, auth, middleware (scope-PAT, rate limit, problem+json)
│   ├── SmartCon.Cloud.Core/             # домен: publish-валидатор, дельта-модель, ключи, квоты (pure, тестируемо)
│   └── SmartCon.Cloud.Data/             # EF Core 10 + Npgsql 10, миграции (Migrations/), PG 18
├── contracts/
│   └── manifest-v1.sample.json          # golden-образец (клиентские unit-тесты читают его же)
├── tests/
│   ├── SmartCon.Cloud.Tests/            # xUnit + Testcontainers (PG)
│   └── http/                            # Postman/REST-коллекция полного цикла
├── deploy/
│   ├── docker-compose.yml               # Приложение B
│   ├── .env.example                     # без секретов; реальные — env/Docker secrets
│   └── backup/
│       ├── pg-backup.sh                 # pg_dump | age → R2 (ежедневно, cron-контейнер)
│       └── pg-restore-drill.md          # процедура drill (приёмка C1)
└── docs/
    └── runbook.md                       # ротация JWT-ключей, emergency-компрометация, restore, переезд VPS
```

## Приложение B. Минимальный docker-compose.yml (эскиз, артефакт C0)

```yaml
services:
  api:
    build: ../src/SmartCon.Cloud.Api
    restart: unless-stopped
    environment:
      ConnectionStrings__Pg: Host=postgres;Database=smartcon_cloud;Username=smartcon;Password=${PG_PASSWORD}
      R2__Endpoint: https://${CF_ACCOUNT_ID}.r2.cloudflarestorage.com
      R2__Bucket: smartcon-cloud-cas
      Jwt__SigningKeyPath: /run/secrets/jwt_signing_key
    secrets: [jwt_signing_key, r2_access]
    # Порты наружу НЕ публикуем — доступ только через cloudflared
  postgres:
    image: postgres:18.6-alpine            # пин минорной версии
    restart: unless-stopped
    # postgres:18+ — единый маунт на /var/lib/postgresql (major-version subdirs,
    # docker-library/postgres#1259; /data-маунт = ошибка старта контейнера)
    volumes: [pgdata:/var/lib/postgresql]
    environment:
      POSTGRES_PASSWORD: ${PG_PASSWORD}
    healthcheck: { test: ["CMD-SHELL", "pg_isready -U smartcon"], interval: 10s }
  cloudflared:
    image: cloudflare/cloudflared:latest   # named tunnel; token в secrets
    restart: unless-stopped
    command: tunnel --no-autoupdate run
    # config.yml туннеля обязан включать: originRequest.chunkedEncoding=true,
    # writeTimeout/readTimeout увеличены (100 МБ/запрос лимит free-прокси +
    # буферизация тела — см. §14 верификацию Tunnel)
    secrets: [tunnel_token]
  backup:
    build: ../deploy/backup
    restart: unless-stopped                # ежедневный pg_dump|age → R2 + алерт в Kuma (push)
  uptime-kuma:
    image: louislam/uptime-kuma:1
    restart: unless-stopped
    volumes: [kuma:/app/data]
volumes: { pgdata: {}, kuma: {} }
secrets:
  jwt_signing_key: { file: ./secrets/jwt_signing_key }
  r2_access:       { file: ./secrets/r2_access }
  tunnel_token:    { file: ./secrets/tunnel_token }
```

## Приложение C. Таблица констант и имён (канон, артефакт C0)

| Константа | Значение |
|---|---|
| Default endpoint | `https://cloud.{домен-владельца}` (поддомен существующего домена в Cloudflare) |
| API base path | `/v1` (версионирование — префикс пути; v2 при ломающих изменениях) |
| Credential Manager target | `AGK.SmartCon.Cloud:{endpoint}:{accountId}` |
| User-Agent клиента | `SmartCon-CloudClient/{pluginVersion}` |
| Формат манифеста | `smartcon.cloud.catalog-manifest`, formatVersion=1 |
| Формат приглашения | `smartcon-cloud:subscribe:{base64url(json)}` |
| Префикс ключей доступа | `SCCAT-` (Crockford base32, 4×4 группы) |
| CAS object key | `objects/sha256/{hex}`; манифесты: `manifests/{catalogId}/{seq}.json.gz` |
| Локальные пути | копии: `%APPDATA%\SmartCon\FamilyManager\cloud\{slug}\`; кэш: `cloud-cache\sha256\`, `cloud-cache\previews\{sha256}\` (LRU 2 ГБ) |
| registry.json | `DatabaseConnection.CloudLink` (skip-unknown совместимость) |
| database_meta | `remote_source_json` (дубль-связка, self-heal) |
| Scope логирования | `BeginScope("Cloud", …)` — OpId сквозь publish/sync |
