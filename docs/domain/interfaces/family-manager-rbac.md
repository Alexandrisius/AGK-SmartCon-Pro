---
module: family-manager-rbac-interfaces
---
# RBAC интерфейсы FamilyManager

> Загружать: при работе с Role-Based Access Control для локальных каталогов.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IDbUserRepository

CRUD для таблицы `db_users` + операции с ownership. Thread-safe через SQLite-транзакции.

**Файл:** `IDbUserRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalDbUserRepository.cs`

```csharp
public interface IDbUserRepository
{
    Task<DbUser?> GetUserAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<DbUser>> GetAllUsersAsync(CancellationToken ct = default);
    Task<DbUser> GetOrCreateUserAsync(UserIdentity identity, CancellationToken ct = default);
    Task<bool> UpdateUserRoleAsync(string userId, DbUserRole role, CancellationToken ct = default);
    Task<bool> UpdateUserStatusAsync(string userId, DbUserStatus status, CancellationToken ct = default);
    Task<bool> RemoveUserAsync(string userId, CancellationToken ct = default);
    Task<int> GetUserCountAsync(CancellationToken ct = default);
    Task<bool> TransferOwnershipAsync(string currentOwnerUserId, string newOwnerUserId, CancellationToken ct = default);
    Task<string?> GetOwnerIdentityAsync(CancellationToken ct = default);
}
```

---

## IDbAccessControlService

Проверка прав текущего пользователя. Кеширует пользователя в volatile-поле, обновляется через `RefreshCurrentUserAsync`.

**Файл:** `IDbAccessControlService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/DbAccessControlService.cs`

```csharp
public interface IDbAccessControlService
{
    Task<DbUserRole> GetCurrentUserRoleAsync(CancellationToken ct = default);
    Task<DbUser> GetCurrentUserAsync(CancellationToken ct = default);
    bool CanImport { get; }
    bool CanEdit { get; }
    bool CanManageUsers { get; }
    bool CanLoadToProject { get; }
    bool IsEditorRole { get; }
    bool IsOwner { get; }
    bool IsBanned { get; }
    Task RefreshCurrentUserAsync(CancellationToken ct = default);
}
```

`CanImport`/`CanEdit`/`CanManageUsers` AND'ят гейт совместимости (ADR-058): на базе,
обновлённой более новым плагином, все write-возможности выключены для любой роли.
`IsEditorRole` — ролевой флаг «Owner/BimMaster и не забанен» БЕЗ compat-AND'а:
нужен UI, адресованному только пишущим ролям (compat-баннер для Инженера
бессмыслен — он и так read-only). `CanLoadToProject` compat-гейтом не трогается
(ярусная модель: загрузка в проект не пишет в каталог).

---

## IUserIdentityService

Идентификация текущего пользователя на основе `Environment.UserName` и `Environment.MachineName`. НЕ требует открытый документ Revit.

**Файл:** `IUserIdentityService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitUserIdentityService.cs`

```csharp
public interface IUserIdentityService
{
    UserIdentity GetCurrentUserIdentity();
}
```
