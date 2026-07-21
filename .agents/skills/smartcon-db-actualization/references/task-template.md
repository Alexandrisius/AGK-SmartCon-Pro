# Шаблон задачи актуализации (`IDatabaseActualizationTask`)

Полный рабочий шаблон новой задачи. Пример: фича добавила колонку `flange_thickness`,
которую надо заполнить для старых баз из файла. Копируй и меняй детект/apply.

## 1. Класс-задача

`src/SmartCon.FamilyManager/Services/Actualization/MyFeatureActualizationTask.cs`

```csharp
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>my-feature-v1</c>): what it backfills,
/// for which scope (active label, loadable). ADR-054.
/// </summary>
internal sealed class MyFeatureActualizationTask : IDatabaseActualizationTask
{
    private readonly LocalCatalogDatabase _database;
    // + сервисы записи (репозитории), НЕ Revit-объекты

    public MyFeatureActualizationTask(LocalCatalogDatabase database /*, repos */)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public string Id => "my-feature-v1";
    public int Order => 40;                 // после glb-v1 (30), шаг 10
    public bool IsCritical => false;        // реши по таблице в SKILL.md!

    // Детект: «пустота колонок». Scope (здесь: loadable, ACTIVE label) — твой.
    private const string DetectionSql = """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'loadable'
          AND cv.version_label = ci.current_version_label
          AND NOT EXISTS(SELECT 1 FROM my_feature_table x
                          WHERE x.catalog_item_id = ci.id AND x.version_id = cv.id)
        """;

    public async Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM (
                SELECT cv.catalog_item_id, cv.version_label
                {DetectionSql}
                  AND cv.revit_major_version <= @maxRevit
                GROUP BY cv.catalog_item_id, cv.version_label
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }

    public async Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        // НЕ менять без нужды: openability по ВСЕМ вариантам группы,
        // RequiredRevitVersion = MAX over groups of MIN(variant revit).
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(MAX((
                SELECT MIN(cv2.revit_major_version) FROM catalog_versions cv2
                WHERE cv2.catalog_item_id = p.itemId AND cv2.version_label = p.label)), 0)
            FROM (
                SELECT DISTINCT cv.catalog_item_id AS itemId, cv.version_label AS label
                {DetectionSql}
            ) p
            WHERE NOT EXISTS (
                SELECT 1 FROM catalog_versions cv3
                WHERE cv3.catalog_item_id = p.itemId AND cv3.version_label = p.label
                  AND cv3.revit_major_version <= @maxRevit
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return NewerOnlyPendingInfo.None;
        return new NewerOnlyPendingInfo(reader.GetInt32(0), reader.GetInt32(1));
    }

    public async Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT cv.catalog_item_id, cv.version_label
            {DetectionSql}
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0) + "|" + reader.GetString(1));
        }
        return keys;
    }

    public Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default)
        => Task.FromResult(0);   // почти всегда 0; file-free = мгновенные UPDATE без файлов

    public async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        // Пишем своё из context.Snapshot / context.Geometry.
        // Данные — на КАЖДЫЙ вариант label (контент идентичен между вариантами).
        foreach (var variant in context.Group.Variants)
        {
            // await _myRepo.UpsertAsync(context.Group.CatalogItemId, variant.VersionId, value, ct);
        }
        // КРИТИЧНО: записанный артефакт обязан погасить DetectionSql,
        // иначе группа останется pending навсегда (вечный янтарь/гейт).
        await Task.CompletedTask;
    }

    public Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        // Вариант А (ретрай, рекомендуется для данных): ничего — детект сам
        // сработает снова при следующем запуске (транзиентные ошибки самозаживают).
        return Task.CompletedTask;

        // Вариант Б (терминальный маркер, как у hash): UPDATE marker колонки
        // (-1 extraction failed / -2 missing) — детект обязан их исключать.
    }
}
```

## 2. DI

`ServiceRegistrar.cs`, секция «Database actualization engine»:

```csharp
services.AddSingleton<IDatabaseActualizationTask, MyFeatureActualizationTask>();
```

## 3. Тесты (обязательный минимум)

`src/SmartCon.Tests/FamilyManager/Services/MyFeatureActualizationTaskTests.cs` —
фикстура `TempCatalogFixture` + `CatalogSeedHelper` (сиды готовые):

```csharp
[Fact] CountPending_BareRow_Pending()                    // детект видит пустоту
[Fact] CountPending_Filled_NotPending()                  // здоровая строка не pending
[Fact] CountNewerOnly_2026Row_NewerOnlyNotProcessable()  // newer-only + RequiredRevitVersion
[Fact] Apply_WritesArtifact_AndClearsDetection()         // ПОСЛЕ apply re-count == 0 !!!
[Fact] LoadPendingKeys_ScopeIsRespected()                // active-only/system — по твоему scope
```

Тест «apply гасит детект» — главный: без него вечный pending ловится только руками в Revit.

## 4. Чеклист перед коммитом

- [ ] Детект — SQL COUNT/keys, без побочных эффектов (вызывается на каждом переключении базы)
- [ ] Apply идемпотентен (DELETE+INSERT или UPSERT)
- [ ] Apply гасит детект (есть тест!)
- [ ] Короткие транзакции, соединения через `LocalCatalogDatabase` (I-14, без WAL)
- [ ] Решение critical/optional задокументировано в XML-doc класса (почему)
- [ ] `Order` — следующее число с шагом 10
- [ ] Тесты зелёные, сборки R19/R21/R24/R25 0/0
- [ ] `docs/architecture/database-migrations.md` — строка в таблице задач
- [ ] Domain docs (interfaces/models) + `validate-docs.ps1` PASSED
- [ ] Ручной тест на сломанной БД в Revit (см. gotchas-and-testing.md)
