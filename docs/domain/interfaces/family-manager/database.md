---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — База данных и миграции

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IDatabaseManager

Управление подключениями к базам данных каталога. Registry хранится в `%APPDATA%\SmartCon\FamilyManager\registry.json`.

**Файл:** `IDatabaseManager.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/DatabaseManager.cs`

```csharp
public interface IDatabaseManager
{
    Task InitializeAsync(CancellationToken ct = default);
    IReadOnlyList<DatabaseConnection> ListConnections();
    DatabaseConnection? GetActiveConnection();
    string? GetActiveDatabasePath();
    Task<DatabaseConnection> CreateDatabaseAsync(string name, string path, CancellationToken ct = default);
    Task<DatabaseConnection> CreateProjectDatabaseAsync(string name, string path, ProjectBaseBinding binding, CancellationToken ct = default);
    Task<DatabaseConnection> ConfigureProjectBaseAsync(string connectionId, ProjectBaseBinding binding, CancellationToken ct = default);
    Task<DatabaseConnection> ConnectDatabaseAsync(string path, CancellationToken ct = default);
    Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default);
    Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default);
    Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default);
    event EventHandler<string>? ActiveDatabaseChanged;
}
```

---

## IRegistryMigrator

Мигратор `registry.json` FamilyManager между версиями схемы. Каждая версия —
небольшое аддитивное обновление (заполнение новых полей значениями по умолчанию
+ атомарная перезапись файла). Запускается один раз при старте плагина сразу
после инициализации `IDatabaseManager`, до того как UI начнёт читать реестр
(см. #119, decision A12).

**Файл:** `IRegistryMigrator.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/RegistryMigrator.cs`

```csharp
public interface IRegistryMigrator
{
    Task MigrateAsync(CancellationToken ct = default);
    int LatestSchemaVersion { get; }
}
```

---

## IProjectBaseBindingEvaluator

Чистый C#-evaluator привязки проектной базы к имени файла Revit. Делегирует парсинг
в `IFileNameParser` из Core.

**Файл:** `IProjectBaseBindingEvaluator.cs`  
**Реализация:** `SmartCon.Core/Services/Implementation/ProjectBaseBindingEvaluator.cs`

```csharp
public interface IProjectBaseBindingEvaluator
{
    ProjectBaseMatch Evaluate(ProjectBaseBinding? binding, string filePath);
}
```

Возвращает `NotApplicable` если `binding` равен `null`; `Match` при успешном парсинге
и прохождении валидации полей; `Mismatch` с пояснением при ошибке. См. ADR-045.

---

## IProjectBaseActivator

Сервис автоматической активации базы при смене активного документа Revit. Pure C#,
не обращается к Revit API напрямую — переключение выполняет `IDatabaseManager.SwitchDatabaseAsync`.

**Файл:** `IProjectBaseActivator.cs`  
**Реализация:** `SmartCon.Core/Services/Implementation/ProjectBaseActivator.cs`

```csharp
public interface IProjectBaseActivator
{
    Task<string?> ActivateForDocumentAsync(string currentFilePath, CancellationToken ct = default);
}
```

Алгоритм: перебрать проектные базы, активировать первую подходящую; иначе fallback
на первую общую базу. Если общих баз нет — возвращает `null`.
