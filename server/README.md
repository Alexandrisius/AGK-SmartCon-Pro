# SmartCon.Cloud — сервер Облачного каталога

> **Лицензия:** эта папка (`server/`) лицензирована под **Business Source License 1.1**
> (см. [LICENSE](LICENSE)): запрещён конкурирующий коммерческий хостинг, конвертация в
> Apache-2.0 через 36 месяцев. Плагин вне `server/` остаётся под MIT (корневой LICENSE).
> Решение владельца 2026-08-26, мастер-план §13.16.

## Текущий статус: вертикальный срез (pre-C0)

Цель среза — **быстрый end-to-end вертикальный срез контракта** Cloud Catalog:
аккаунты → каталог → upload CAS → publish point → подписка → manifest/latest →
resolve/download → верификация SHA-256. Инфраструктурные части плана (R2, tunnel)
заменены dev-реализациями за стабильным интерфейсом. Реализовано по
`docs/family-manager/02-plans/cloud-catalog-master-plan.md` и ADR-075/076/077
(в корне репозитория).

### Что вошло в срез

| Компонент | Реализация |
|---|---|
| Стек | ASP.NET Core 10 (net10.0), EF Core 10 + Npgsql (PG 18), ASP.NET Core Identity |
| Auth | register/login (**no email-enumeration**), JWT access 30 мин, **rotating refresh** с reuse-детектом (E16), logout |
| Каталоги | create (slug immutable, 409 при коллизии), publish с **атомарной Serializable-транзакцией** и stale-seq 409, manifest/latest |
| CAS | `IObjectStorage` → dev-реализация на локальной ФС (`{root}/{sha256[..2]}/{sha256}`); streaming SHA-256 при upload, **checksum_mismatch 400**; дедуп (повторный upload идемпотентен) |
| Подписка | идемпотентный stub (restore, E30) |
| Пробник | `tools/SmartCon.Cloud.Probe` (net8.0 = TFM плагина R25/R26): полный позитивный + негативный сценарий |

### Осознанные отступления от плана (закрываются в C0/C1)

| Отступление среза | Полная реализация по плану |
|---|---|
| CAS на ФС, файлы через API | Cloudflare R2 + presigned URL (ADR-075 §4; `IObjectStorage` — точка подмены, контракт не меняется) |
| publish = `{manifest}` (полный снапшот) | дельта `{basePublishSeq, kind, changes}` + per-item курсоры (ADR-077 §3) |
| Серверная валидация манифеста: `formatVersion`/`publishSeq`/наличие CAS-объектов | полный набор §6.3: дедуп content_hash, иммутабельность версий, Dependency-валидация, PII-фильтр |
| Подписка без ключей/статусов | ключи SCCAT, maxActivations, suspend, check-in (ADR-076 §3) |
| `manifest/latest` без per-catalog authz на resolve | анти-оракулы, per-catalog подписка на каждый sha256 (ADR-076 §4) |
| PUT с `checksum_mismatch` оставляет blob в CAS без строки в `cas_objects` | orphan-GC по окну (§6.5); сам CAS content-addressed — утечки доступа нет |
| `Jwt:SigningKeyHex` в `appsettings.Development.json` — публичный dev-ключ | Production: Docker secrets / env (compose требует `JWT_SIGNING_KEY_HEX`) |
| GET `/v1/files/{sha256}` анонимный | presigned-семантика среза: URL = токен (R2 presigned GET, ADR-075 §4); TTL/ротация URL — C1 |
| `EnsureCreated()` | EF Core миграции (C1) |
| Без gzip манифеста, квот, rate limit, бэкапов, мониторинга | §5/§6.4/§6.5 плана |

## Как поднять (dev)

Порты выбраны вне занятых локальным Docker-стеком (3000-3002, 5432, 6379, 8000,
9000-9001…) и биндятся **только на 127.0.0.1**: API `8787`, PostgreSQL `5433`.

```bash
# 1) PostgreSQL (конфиг deploy/docker-compose.yml)
cd server/deploy && docker compose up -d postgres

# 2) API (локальный запуск — быстрый цикл; Production-режим: docker compose up -d api)
cd server/src/SmartCon.Cloud.Api && dotnet run

# 3) Вертикальный срез (другой терминал)
cd server/tools/SmartCon.Cloud.Probe && dotnet run
# или против другого эндпоинта: dotnet run -- http://127.0.0.1:8787
```

Ожидаемый итог пробника: `СРЕЗ ПРОЙДЕН ✅` (exit code 0). OpenAPI в dev-режиме:
`http://127.0.0.1:8787/openapi/v1.json`, health: `/healthz`.

Смена портов — через `deploy/.env` (`HOST_API_PORT`, `HOST_PG_PORT`; образец `.env.example`).
`JWT_SIGNING_KEY_HEX` обязателен для Production-профиля compose (64 hex-символа, `openssl rand -hex 32`).

## Структура

```
server/
├── LICENSE                    # BSL 1.1
├── global.json                # pin .NET SDK 10
├── Directory.Packages.props   # CPM сервера (отдельный от src/)
├── src/SmartCon.Cloud.Api/    # minimal API: Auth/Cas/Data/Domain/Endpoints
├── tools/SmartCon.Cloud.Probe/# вертикальный срез (net8.0)
└── deploy/                    # docker-compose (postgres + api), .env.example
```
