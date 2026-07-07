---
module: family-manager-rbac
---
# RBAC модели FamilyManager

> Загружать: при работе с системой Role-Based Access Control для локальных каталогов.
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/Rbac/*.cs`.

Модели системы Role-Based Access Control для локальных каталогов FamilyManager.

## DbUserRole

Роль пользователя в каталоге. Определяет уровень доступа.

**Файл:** `DbUserRole.cs`

```csharp
public enum DbUserRole
{
    Owner = 0,
    BimMaster = 1,
    Engineer = 2
}
```

---

## DbUserStatus

Статус пользователя: активен или заблокирован.

**Файл:** `DbUserStatus.cs`

```csharp
public enum DbUserStatus
{
    Active = 0,
    Banned = 1
}
```

---

## DbUser

Запись пользователя каталога с ролью и статусом.

**Файл:** `DbUser.cs`

```csharp
public sealed record DbUser(
    string UserId,
    string DisplayName,
    DbUserRole Role,
    DbUserStatus Status,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset LastSeenAtUtc
);
```

---

## UserIdentity

Идентификация текущего пользователя. Формат UserId: `"{UserName}@{MachineName}"`.

**Файл:** `UserIdentity.cs`

```csharp
public sealed record UserIdentity(
    string UserId,
    string DisplayName,
    string MachineName,
    string UserName
);
```

---

## DbAccessDeniedException

Исключение, выбрасываемое когда текущий пользователь заблокирован (Banned). Поддерживает NET48 serialization.

**Файл:** `DbAccessDeniedException.cs`

```csharp
public sealed class DbAccessDeniedException : Exception
{
    public string DbName { get; }
    public string OwnerDisplayName { get; }
}
```
