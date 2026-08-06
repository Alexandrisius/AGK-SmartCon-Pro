---
module: family-manager
---
# Модели FamilyManager — Зависимости (ADR-066)

> Загружать: при работе с routing-фитингами, shared nested families, `family_dependencies`.
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/FamilyDependency*.cs`.

Единая модель зависимостей родитель→ребёнок (EPIC #207, ADR-066, схема V29):
routing-фитинги системных линейных категорий (E1) и shared nested families
loadable-родителей (E2). Зависимость — полноценный элемент каталога
(версии, хэш, валидация, ES-маркер); таблица `family_dependencies` хранит
только СВЯЗИ. Связь version-scoped со стороны родителя (история/аудит);
выбор версии ребёнка при sync — всегда активная (`current_version_label`).

## FamilyDependencyKind

Дискриминатор классов зависимостей (`family_dependencies.dependency_kind`).
Строковые константы (не enum): колонка — plain TEXT, новые kind'ы не требуют
смены схемы.

**Файл:** `Models/FamilyManager/FamilyDependencyKind.cs`

```csharp
public static class FamilyDependencyKind
{
    public const string Routing = "routing";             // фитинг из RoutingPreferenceManager
    public const string SharedNested = "shared_nested";  // вложенное shared-семейство (E2)
}
```

---

## FamilyDependencyInfo

Одна связь parent→child между элементами каталога (строка
`family_dependencies`). `PartName` — оригинальный токен `"Family:Type"`
routing-правила (матчинг правила на связь без повторного парсинга снапшота
родителя); `null` для других kind'ов.

**Файл:** `Models/FamilyManager/FamilyDependencyInfo.cs`

```csharp
public sealed record FamilyDependencyInfo(
    string ChildCatalogItemId,
    string Kind,
    string? PartName,
    int Ordinal);
```

---

## FamilyDependencyDescriptor

Identity семейства-зависимости, обнаруженного на Phase-1 prepare.
`FamilyUniqueId` — единственный ключ downstream-стадий (EditFamily по
UniqueId, никогда по имени — I-05, #183).

**Файл:** `Models/FamilyManager/FamilyDependencyDescriptor.cs`

```csharp
public sealed record FamilyDependencyDescriptor(
    string Kind,
    string? PartName,
    string FamilyUniqueId,
    string FamilyName,
    string? CategoryName);
```

---

## FamilyDependencyLink

Декларация «эта строка батча — зависимость другой строки»: ребёнок несёт
`ParentSourcePath` (= `PreparedFamilyItem.SourcePath` /
`FamilyBatchImportItem.FilePath` родителя). Создаётся на Phase-1, едет
через диалог неизменной, записывается Phase-3 executor'ом в
`family_dependencies` после импорта обеих сторон.

**Файл:** `Models/FamilyManager/FamilyDependencyLink.cs`

```csharp
public sealed record FamilyDependencyLink(
    string ParentSourcePath,
    string Kind,
    string? PartName);
```

---

## FamilyDependencyReference

Одна ВХОДЯЩАЯ ссылка на элемент каталога (E5, #213, ADR-067): какая версия
какого родителя объявляет элемент зависимостью. Результат reverse-запроса
`IFamilyDependencyRepository.GetReferencingParents[Batch]Async` — читается
по ВСЕМ версиям (архивная блокирует удаление наравне с активной).
Используется dependency guard'ом (блок-диалог удаления) и скрепкой в дереве.

**Файл:** `Models/FamilyManager/FamilyDependencyReference.cs`

```csharp
public sealed record FamilyDependencyReference(
    string ParentCatalogItemId,
    string ParentName,
    string VersionLabel,
    bool IsCurrentVersion);
```
