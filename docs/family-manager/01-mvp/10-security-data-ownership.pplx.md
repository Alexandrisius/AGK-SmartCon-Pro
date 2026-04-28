# FamilyManager Security and Data Ownership

## Цель документа

FamilyManager работает с BIM-контентом, который может быть интеллектуальной собственностью пользователя или компании. Даже локальный MVP должен иметь ясную модель владения данными.

## Data Ownership

| Data | Owner | Storage | Notes |
| --- | --- | --- | --- |
| `.rfa` file | User / organization | source path or cache | Не отправлять наружу в MVP |
| Catalog metadata | User | SQLite | Можно экспортировать вручную |
| Tags/status/description | User | SQLite | Локальные правки |
| Preview | Generated cache | File cache | Можно пересоздать |
| Project usage | User / organization | SQLite in MVP; server DB later | Не хранится в ExtensibleStorage |
| Logs | User machine | smartCon logs | Без токенов |
| Credentials | User | Not MVP | Later Windows Credential Manager |
| Server permissions | Organization | Server DB | Not MVP |

## MVP Security Rules

1. MVP не отправляет `.rfa` файлы на сервер.
2. MVP не хранит токены.
3. MVP не содержит embedded credentials.
4. Logs не должны содержать секреты.
5. Ошибки не должны показывать stack trace обычному пользователю.
6. SQLite база не должна удаляться при update smartCon.
7. File cache должен иметь понятный путь и recovery strategy.
8. Import/export выполняется только явным действием пользователя.

## Sensitive Data

| Data | Sensitivity | Handling |
| --- | --- | --- |
| Absolute file paths | Medium | Можно логировать осторожно; лучше сокращать |
| Corporate family files | High | Не отправлять в MVP |
| Manufacturer metadata | Low/Medium | Хранить в SQLite |
| User name | Medium | Не обязательно хранить |
| Server token | High | Только future credential abstraction |
| Project GUID | Medium | Хранить только если полезно |

## File Cache Policy

Recommended MVP options:

| Mode | Description | Default |
| --- | --- | --- |
| Linked | Store original path only | Для первого MVP проще |
| Cached | Copy to FamilyManager cache | Надёжнее для переносимости |
| Hybrid | Keep original path + cached copy | Recommended после MVP |

MVP должен принять одно решение:

- если выбрать `Linked`, нужно явно показывать Missing file state;
- если выбрать `Cached`, нужно контролировать размер cache;
- если выбрать Hybrid, возрастает сложность import.

## SQLite Protection

Канонический локальный root MVP: `%APPDATA%\AGK\SmartCon\FamilyManager\`.

MVP не обязан шифровать локальную базу, но должен:

- хранить её в документированном месте;
- делать backup перед destructive migration;
- не писать туда секреты;
- позволять пользователю найти путь к базе;
- не удалять базу uninstall/update скриптами без явного действия.

## Server Phase Security Preview

Remote/corporate phase потребует:

- HTTPS only;
- JWT/OIDC-compatible auth;
- refresh token storage in Windows Credential Manager;
- organization ID;
- provider permissions;
- audit log;
- content visibility: Private / Organization / Public;
- upload/download policies.

## Logging Rules

Use `SmartConLogger` only.

Log:

- operation name;
- result;
- elapsed time;
- number of files;
- exception type/message.

Avoid:

- tokens;
- full credentials;
- raw HTTP auth headers;
- large file content;
- unnecessary full paths in enterprise mode.

## Legal/IP Notes for Future

Public sharing and marketplace are out of MVP because they require:

- EULA/Terms;
- content ownership confirmation;
- takedown policy;
- manufacturer content permissions;
- moderation;
- license metadata per content item.
