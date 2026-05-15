# ADR-021: FamilyManager RBAC — Role-Based Access Control для локальных каталогов

**Статус:** accepted
**Дата:** 2026-05-16

## Контекст

FamilyManager использует локальные SQLite-каталоги (`catalog.db`), которые могут лежать на сетевых дисках (SMB). Несколько проектировщиков работают с одним каталогом одновременно. Нужен механизм контроля доступа без серверной компоненты — только через SQLite.

Требования:
- Идентификация пользователей без паролей и внешних сервисов
- Разграничение прав: Owner / BimMaster / Engineer
- Возможность блокировки нежелательных пользователей (ban)
- Защита от одновременной записи через SQLite WAL mode

## Решение

### Модели (Core, `SmartCon.Core/Models/FamilyManager/`)

- **`DbUserRole`** enum: `Owner=0`, `BimMaster=1`, `Engineer=2`
- **`DbUserStatus`** enum: `Active=0`, `Banned=1`
- **`DbUser`** sealed record: `UserId`, `DisplayName`, `Role`, `Status`, `JoinedAtUtc`, `LastSeenAtUtc`
- **`UserIdentity`** sealed record: `UserId`, `DisplayName`, `MachineName`, `UserName`
- **`DbAccessDeniedException`** : Exception с `DbName` и `OwnerDisplayName` + NET48 serialization support

### Идентификация пользователя

```
UserId = "{Environment.UserName}@{Environment.MachineName}"
```

Уникальный per-machine идентификатор без паролей. `RevitUserIdentityService` реализует `IUserIdentityService`, НЕ требует открытый документ Revit.

### Интерфейсы (Core, `SmartCon.Core/Services/Interfaces/`)

- **`IDbUserRepository`**: CRUD для таблицы `db_users` + `TransferOwnershipAsync` + `GetOwnerIdentityAsync`
- **`IDbAccessControlService`**: `CanImport`, `CanEdit`, `CanManageUsers`, `CanLoadToProject`, `IsOwner`, `IsBanned` + `RefreshCurrentUserAsync`

### Матрица прав

| Действие | Owner | BimMaster | Engineer | Banned |
|---|---|---|---|---|
| Просмотр каталога | ✅ | ✅ | ✅ | ❌ |
| Загрузка в проект | ✅ | ✅ | ✅ | ❌ |
| Импорт файлов | ✅ | ✅ | ❌ | ❌ |
| Редактирование свойств | ✅ | ✅ | ❌ | ❌ |
| Управление пользователями | ✅ | ✅ | ❌ | ❌ |
| Смена роли | ✅ | ❌ | ❌ | ❌ |
| Передача ownership | ✅ | ❌ | ❌ | ❌ |
| Удаление БД | ✅ | ❌ | ❌ | ❌ |

### Реализации

- `LocalDbUserRepository` — thread-safe SQLite, UNIQUE constraint race handling, transactions для TOCTOU-safe операций
- `DbAccessControlService` — volatile `_cachedUser`, lazy refresh

### Ключевые паттерны

- **Auto-register**: новый пользователь получает роль Engineer автоматически при первом подключении
- **Ban ≠ Remove**: Ban сохраняет запись (блокирует повторное подключение), Remove удаляет (позволяет переподключиться как Engineer)
- **Ownership transfer**: атомарная транзакция (old→BimMaster, new→Owner, update owner_identity)
- **Schema v7**: миграция добавляет `db_users` table + `owner_identity` column в `catalog_metadata`
- **Event suppression**: при CreateDatabase/SwitchDatabase/ConnectDatabase — suppression `ActiveDatabaseChanged` + явный `RefreshAccessAndLoadTreeAsync`

### UI

- `ProfileViewModel` — управление пользователями (список, смена ролей, бан, удаление, передача ownership)
- `FamilyPropertiesViewModel.IsReadOnly` — gate для Engineer (TextBox IsReadOnly, ComboBox IsEnabled, кнопки через CanWrite())
- SVG Path иконки для ban/remove кнопок
- `[NotifyCanExecuteChangedFor]` на CanImport/CanEdit/CanManageUsers в главном VM

## Последствия

**Плюсы:**
- Offline-first, нет сервера
- WAL mode поддерживает 1-2 writers + 50-100 readers по SMB
- Простая идентификация без паролей — достаточно имени пользователя и машины

**Минусы:**
- Нет real-time уведомлений о смене прав — пользователь должен переподключиться
- Идентификация на основе имени машины — теоретически можно подделать

## Альтернативы (отклонённые)

1. **JWT/OAuth авторизация** — требует серверную компоненту, противоречит offline-first архитектуре
2. **Windows Active Directory groups** — не все команды используют AD, усложняет настройку
3. **Хранение прав per-row** — избыточно для каталога семейств, достаточно роли на уровне БД

## Связанные инварианты

- **I-01**: Revit API из WPF — только через `IExternalEventHandler`. RBAC-операции не вызывают Revit API.
- **I-03**: Транзакции Revit — RBAC использует SQLite-транзакции, не Revit Transaction.
- **I-05**: `DbUser` и `UserIdentity` не хранят Revit-объекты — только string-идентификаторы.
- **I-09**: Все модели RBAC — чистый C# в `SmartCon.Core`, без WPF/Revit зависимостей.
- **I-10**: MVVM строго — `ProfileViewModel` управляет UI, View содержит только `DataContext = viewModel`.
- **I-14**: Thread safety — `LocalDbUserRepository` использует SQLite-транзакции для TOCTOU-safe операций.

## Связанные файлы

- `src/SmartCon.Core/Models/FamilyManager/DbUserRole.cs`
- `src/SmartCon.Core/Models/FamilyManager/DbUserStatus.cs`
- `src/SmartCon.Core/Models/FamilyManager/DbUser.cs`
- `src/SmartCon.Core/Models/FamilyManager/UserIdentity.cs`
- `src/SmartCon.Core/Models/FamilyManager/DbAccessDeniedException.cs`
- `src/SmartCon.Core/Services/Interfaces/IDbUserRepository.cs`
- `src/SmartCon.Core/Services/Interfaces/IDbAccessControlService.cs`
- `src/SmartCon.Core/Services/Interfaces/IUserIdentityService.cs`
- `src/SmartCon.Revit/FamilyManager/RevitUserIdentityService.cs`
- `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalDbUserRepository.cs`
- `src/SmartCon.FamilyManager/Services/DbAccessControlService.cs`
- `src/SmartCon.FamilyManager/ViewModels/ProfileViewModel.cs`
- `src/SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.cs`
- `src/SmartCon.FamilyManager/ViewModels/DbUserItem.cs`
- `src/SmartCon.Tests/FamilyManager/Services/DbAccessControlServiceTests.cs`
