# Шаблон задачи актуализации (`IDatabaseActualizationTask`)

Минимальный рабочий шаблон новой задачи на базовом классе `SqlDetectionActualizationTaskBase`.
Пример: фича добавила колонку `flange_thickness`, которую надо заполнить для старых баз из файла.
Копируй и меняй детект/apply. **Три метода-детекта, file-free pass и retry-политику даёт база —
не копируй их из старых задач.**

## 1. Класс-задача (~50 строк)

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
internal sealed class MyFeatureActualizationTask : SqlDetectionActualizationTaskBase
{
    // + сервисы записи (репозитории) в ctor, НЕ Revit-объекты
    public MyFeatureActualizationTask(LocalCatalogDatabase database /*, repos */)
        : base(database)
    {
    }

    public override string Id => "my-feature-v1";
    public override int Order => 60;                // после family-facts-v1 (50), шаг 10
    public override bool IsCritical => false;       // реши по таблице в SKILL.md!

    // Детект: FROM/JOIN/WHERE фрагмент по «пустоте колонок».
    // Группировка (item|label), фильтр Revit, newer-only — в базовом классе.
    // Scope (здесь: loadable, ACTIVE label) — твой.
    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'loadable'
          AND cv.version_label = ci.current_version_label
          AND NOT EXISTS(SELECT 1 FROM my_feature_table x
                          WHERE x.catalog_item_id = ci.id AND x.version_id = cv.id)
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        // Пишем своё из context.Snapshot / context.Geometry.
        // Данные — на КАЖДЫЙ вариант label (контент идентичен между вариантами);
        // ресинк catalog_items — только когда context.Group.IsActiveLabel.
        foreach (var variant in context.Group.Variants)
        {
            // await _myRepo.UpsertAsync(context.Group.CatalogItemId, variant.VersionId, value, ct);
        }
        // КРИТИЧНО: записанный артефакт обязан погасить DetectionSql,
        // иначе группа останется pending навсегда (вечный янтарь/гейт).
        await Task.CompletedTask;
    }
}
```

### Когда нужен override поверх базы

| Член | Дефолт базы | Когда переопределять |
|---|---|---|
| `RunFileFreePassAsync` | `Task.FromResult(0)` | Мгновенные UPDATE без открытия файлов (hash: system re-flag) |
| `HandleGroupFailureAsync` | `Task.CompletedTask` (retry в след. запуске) | Терминальный маркер (hash: -1/-2) — тогда детект обязан его исключать (`NOT IN (2,-1,-2)`) |
| `CountPendingAsync` | count processable групп из `DetectionSql` | Доп. счётчики вне групповой модели (hash: + system rows) |
| `LoadPendingGroupKeysAsync` / `GetNewerOnlyPendingAsync` | из `DetectionSql` | Почти никогда — семантика инвариантна движку |

Образец override'ов: `HashFormatActualizationTask`.

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

- [ ] Детект — SQL-фрагмент FROM/JOIN/WHERE, без побочных эффектов (вызывается на каждом переключении базы)
- [ ] Apply идемпотентен (DELETE+INSERT или UPSERT)
- [ ] Apply гасит детект (есть тест!)
- [ ] Короткие транзакции, соединения через унаследованное `Database` (I-14, без WAL)
- [ ] Решение critical/optional задокументировано в XML-doc класса (почему)
- [ ] `Order` — следующее число с шагом 10
- [ ] Тесты зелёные, сборки R19/R21/R24/R25 0/0
- [ ] `docs/architecture/database-migrations.md` — строка в таблице задач
- [ ] Domain docs (interfaces/models) + `validate-docs.ps1` PASSED
- [ ] Ручной тест на сломанной БД в Revit (см. gotchas-and-testing.md)
