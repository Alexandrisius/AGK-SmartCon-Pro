---
module: project-management
---
# Модели ProjectManagement (Share Project)

> Загружать: при работе с модулем Share Project (ISO 19650).
> Источник истины: `src/SmartCon.Core/Models/*.cs` (ShareProject*, FileNameTemplate, FieldDefinition, PurgeOptions, ViewInfo).

## ShareProjectSettings

Корневая модель настроек модуля ShareProject. Хранится per-project в ExtensibleStorage (ADR-013).

**Файл:** `SmartCon.Core/Models/ShareProjectSettings.cs`

```csharp
public sealed record ShareProjectSettings
{
    public string ShareFolderPath { get; init; } = string.Empty;
    public FileNameTemplate FileNameTemplate { get; init; } = new();
    public List<FieldDefinition> FieldLibrary { get; init; } = [];
    public PurgeOptions PurgeOptions { get; init; } = new();
    public List<string> KeepViewNames { get; init; } = [];
    public bool SyncBeforeShare { get; init; } = true;
    public static ShareProjectSettings Empty => new();
}
```

---

## FileNameTemplate

Шаблон разбора имени файла: разделитель + блоки + маппинг статусов.

**Файл:** `SmartCon.Core/Models/FileNameTemplate.cs`

```csharp
public sealed record FileNameTemplate
{
    public string Delimiter { get; init; } = "-";
    public List<FileBlockDefinition> Blocks { get; init; } = [];
    public List<StatusMapping> StatusMappings { get; init; } = [];
}
```

---

## FileBlockDefinition

Определение блока имени файла с ролью и меткой.

**Файл:** `SmartCon.Core/Models/FileBlockDefinition.cs`

```csharp
public sealed record FileBlockDefinition
{
    public int Index { get; init; }
    public string Field { get; init; } = "";
    public string Label { get; init; } = "";
    public ParseRule? ParseRule { get; init; }
    public List<string> AllowedValues { get; init; } = [];
}
```

---

## FieldDefinition

Определение поля из библиотеки — переиспользуемое описание с валидацией.

**Файл:** `SmartCon.Core/Models/FieldDefinition.cs`

```csharp
public sealed record FieldDefinition
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public ValidationMode ValidationMode { get; init; }
    public List<string> AllowedValues { get; init; } = [];
    public int? MinCharCount { get; init; }
    public int? MaxCharCount { get; init; }
}
```

---

## PurgeOptions

Настраиваемый список категорий очистки для Shared-файла.

**Файл:** `SmartCon.Core/Models/PurgeOptions.cs`

```csharp
public sealed record PurgeOptions
{
    public bool PurgeRvtLinks { get; init; } = true;
    public bool PurgeCadImports { get; init; } = true;
    public bool PurgeImages { get; init; } = true;
    public bool PurgePointClouds { get; init; } = true;
    public bool PurgeGroups { get; init; } = true;
    public bool PurgeAssemblies { get; init; } = true;
    public bool PurgeSpaces { get; init; } = true;
    public bool PurgeRebar { get; init; } = true;
    public bool PurgeFabricReinforcement { get; init; } = true;
    public bool PurgeUnused { get; init; } = true;
}
```

---

## ShareProjectResult

Результат операции ShareProject.

**Файл:** `SmartCon.Core/Models/ShareProjectResult.cs`

```csharp
public sealed record ShareProjectResult
{
    public bool Success { get; init; }
    public string? SharedFilePath { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? ErrorMessage { get; init; }
    public int ElementsDeleted { get; init; }
    public int PurgedElementsCount { get; init; }
}
```

---

## ViewInfo

Информация о виде для отображения в UI настроек.

**Файл:** `SmartCon.Core/Models/ViewInfo.cs`

```csharp
public sealed class ViewInfo
{
    public string Name { get; init; } = string.Empty;
    public ElementId Id { get; init; }
    public string ViewType { get; init; } = string.Empty;
}
```

---

## ParseMode

Режим парсинга сегмента имени файла.

**Файл:** `SmartCon.Core/Models/ParseMode.cs`

```csharp
public enum ParseMode
{
    DelimiterSegment,
    FixedWidth,
    BetweenMarkers,
    AfterMarker,
    Remainder
}
```

---

## ParseRule

Правило парсинга блока имени файла: режим, разделитель, индексы, маркеры.

**Файл:** `SmartCon.Core/Models/ParseRule.cs`

```csharp
public sealed record ParseRule
{
    public ParseMode Mode { get; init; } = ParseMode.DelimiterSegment;
    public string Delimiter { get; init; } = "-";
    public int SegmentIndex { get; init; } = 1;
    public int SegmentCount { get; init; } = 1;
    public int CharOffset { get; init; }
    public int CharCount { get; init; }
    public string OpenMarker { get; init; } = "(";
    public int OpenMarkerIndex { get; init; } = 1;
    public string CloseMarker { get; init; } = ")";
    public int CloseMarkerIndex { get; init; } = 1;
    public string Marker { get; init; } = "-";
    public int MarkerIndex { get; init; } = 1;

    public static ParseRule DefaultDelimiter(string delimiter = "-", int segmentIndex = 1);
}
```

---

## ValidationMode

Режим валидации значения поля.

**Файл:** `SmartCon.Core/Models/ValidationMode.cs`

```csharp
public enum ValidationMode
{
    None,
    AllowedValues,
    Contains,
    CharCount
}
```

| Значение | Описание |
|---|---|
| `None` | Валидация отключена. |
| `AllowedValues` | Значение должно точно совпадать с одним из допустимых (регистр не важен, пробелы по краям игнорируются). |
| `Contains` | Значение должно содержать хотя бы одну из указанных подстрок (регистр не важен, пробелы по краям игнорируются). |
| `CharCount` | Длина значения должна быть в диапазоне `MinLength`..`MaxLength`. |

---

## ValidationResult

Результат валидации имени файла: общий статус, сводка и список результатов по блокам.

**Файл:** `SmartCon.Core/Models/ValidationResult.cs`

```csharp
public sealed record BlockValidation(
    int Index,
    string Field,
    string Value,
    bool IsValid,
    string? Error
);

public sealed record ValidationResult(
    bool IsValid,
    string Summary,
    List<BlockValidation> Blocks
);
```
