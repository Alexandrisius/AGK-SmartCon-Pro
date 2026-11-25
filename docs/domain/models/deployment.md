---
module: deployment
---
# Модели развёртывания и изоляции зависимостей

> Загружать: при работе с изоляцией зависимостей (ADR-051, Issue #134), манифестом `.addin`,
> детектором конфликтов и пайплайном обновлений.
> Источник истины: `src/SmartCon.Core/Deployment/`.

## SmartConAddinManifest

Единый генератор содержимого манифеста `SmartCon.addin`. Используется `AddinManifestHealer`
(self-healing при старте) и служит эталоном для установщика и Updater'а. Для Revit 2026+
включает `ManifestSettings` — нативную изоляцию AssemblyLoadContext. На Revit 2025 тег
запрещён (парсер отвергает весь файл) — там изоляцию выполняет Nice3point.Revit.Toolkit
без участия манифеста.

**Файл:** `SmartCon.Core/Deployment/SmartConAddinManifest.cs`

```csharp
public static class SmartConAddinManifest
{
    public const string AddInId;
    public const string IsolationContextName;           // "SmartCon"
    public const int FirstNativeIsolationRevitYear;     // 2026

    public static string Build(string assemblyPath, bool includeIsolationSettings);
    public static string GetInstallSubfolder(int revitYear);
    public static IReadOnlyList<int> GetManifestYearsForSubfolder(string subfolder);
}
```

---

## DependencyConflictAnalyzer

Анализатор конфликтов зависимостей в окружении Revit. Сравнивает уже загруженные в процесс
сборки с минимальными версиями, с которыми собран SmartCon. После внедрения изоляции
результат носит диагностический характер (идентифицирует чужой конфликтующий адд-ин).

**Файл:** `SmartCon.Core/Deployment/DependencyConflictAnalyzer.cs`

```csharp
public static class DependencyConflictAnalyzer
{
    public static IReadOnlyDictionary<string, Version> MinimumVersions { get; }
    public static IReadOnlyList<DependencyConflict> Analyze(IEnumerable<LoadedDependencyInfo> loadedAssemblies);
}
```

---

## LoadedDependencyInfo

Сведения об уже загруженной в процесс сборке-зависимости (имя, версия, путь).

**Файл:** `SmartCon.Core/Deployment/DependencyConflictAnalyzer.cs`

```csharp
public sealed record LoadedDependencyInfo(string Name, Version Version, string Location);
```

---

## DependencyConflict

Конфликт: загружена версия ниже требуемой SmartCon'ом (с указанием пути-источника).

**Файл:** `SmartCon.Core/Deployment/DependencyConflictAnalyzer.cs`

```csharp
public sealed record DependencyConflict(string Name, Version LoadedVersion, Version MinimumVersion, string Location);
```

---

## AssemblyResolutionPolicy

Политика разрешения identity-critical инфраструктуры BCL на net48 (Issue #527). `System.Memory`
содержит типы `Span`/`Memory`, которые входят в публичные сигнатуры сторонних сборок
(`SQLitePCLRaw.core`, `Microsoft.Data.Sqlite`): в процессе допустима ровно одна физическая
копия, иначе `ReadOnlySpan<T>` вызывающего и метода окажутся разными типами и JIT бросит
`MissingMethodException` (dotnet/roslyn#66104). Приоритет копий: хост (папка Revit) → уже
загруженная → папка плагина. Используется `App.OnAssemblyResolve` и `DependencyGuard`.

**Файл:** `SmartCon.Core/Deployment/AssemblyResolutionPolicy.cs`

```csharp
public static class AssemblyResolutionPolicy
{
    public static bool IsTypeIdentityCritical(string? simpleName);
    public static AssemblyResolutionSource ChooseIdentityCriticalSource(
        bool hostCopyExists, bool loadedCopyExists, bool pluginCopyExists);
}
```

## AssemblyResolutionSource

Источник копии identity-critical сборки при `AssemblyResolve`.

**Файл:** `SmartCon.Core/Deployment/AssemblyResolutionPolicy.cs`

```csharp
public enum AssemblyResolutionSource
{
    None = 0,
    Host = 1,
    Loaded = 2,
    Plugin = 3,
}
```

