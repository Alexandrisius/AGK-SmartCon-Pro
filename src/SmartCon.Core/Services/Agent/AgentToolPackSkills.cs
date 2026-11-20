namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Short markdown skill returned by <c>load_tool_pack</c> together with a pack
/// (#400). It is the operating manual for the pack: glossary of the arguments
/// in Revit UI terms, the invariants a write must respect, and the recipes that
/// worked on real models.
/// </summary>
public static class AgentToolPackSkills
{
    private const string Filters = @"## Пак filters: фильтры видов

Проектный фильтр (`ParameterFilterElement`) — это именованный набор категорий и правил. Он
применяется к видам: `create_view_filter`, `set_filter_overrides`. Просмотр: `list_filters`,
`get_filter_details`. Совместимость параметров: `list_filterable_parameters`.

### Две галочки Revit UI (не путать!)
- **«Фильтр включён»** = `View.GetIsFilterEnabled` / `SetIsFilterEnabled`. Выключен → фильтр
  не действует, overrides сохраняются, элементы видны.
- **«Видимость»** = `View.GetFilterVisibility` / `SetFilterVisibility`. Снята → совпавшие
  элементы скрыты на этом виде.
- `hide` = фильтр включён + «Видимость» снята (элементы скрыты).
- `show` = фильтр включён + «Видимость» стоит (элементы видны, работают только overrides).
- `none` = фильтр **выключен** (`SetIsFilterEnabled(false)`) → элементы снова видны.
- Чтобы вернуть всё как было, **выключите фильтр**, не удаляя его: `create_view_filter`
  с `visibility=""none""` или `set_view_visibility` для категории.

### Аргументы
- `visibility` — только `none|hide|show` по таблице выше. Старое значение `none` раньше
  оставляло фильтр включённым; сейчас оно честно выключает фильтр.
- `categories` в `set_filter_rules` **заменяет** список, а не добавляет к нему. Хотите
  добавить — перечислите старые и новые вместе.
- `conditions` с пустым массивом очищают правила (фильтр остаётся по категориям).

### Инварианты
- Pre-flight: параметр должен быть фильтруемым для **всех** целевых категорий
  (`list_filterable_parameters` → `supportedByAllCategories=true`). Иначе Revit отклонит
  набор, а категории уже изменятся; тулы возвращают `parameter_not_supported_for_categories`.
- `dryRun=true` реально применяет изменение в транзакции и откатывает её: `validated=true`
  означает «Revit принял». Ошибка dry-run = изменение не пройдёт.
- После любого `success=false` — обязательный read-back (`get_filter_details`) до вывода
  «ничего не изменилось»: отчёт может не совпасть с моделью.
- `set_filter_rules` не откатывается `undo_last` (`undoSupported=false`): прежнее дерево
  лежит в `previousRules`, прежние категории — в `previousCategories`. Откат — повторный
  вызов с этими значениями.

### Рецепты
1. Создать фильтр, но пока не скрывать: `create_view_filter(visibility=""none"")`.
2. Включить скрытие: тот же фильтр с `visibility=""hide""` (или `set_filter_rules`).
3. Вернуть видимость: `visibility=""none""` — фильтр остаётся на виде, элементы видны.
4. Проверить эффект: `get_filter_details(includeOverrides=true)` → `filterEnabled` +
   `visible` + `mode`; `query_elements(viewId=...)` для фактического состава.

### Что нельзя
- Добавлять категорию без pre-flight параметра: фитинги и арматура часто не имеют
  параметра системы («Имя системы»), фильтр по нему для них невозможен.
- Верить `success=false` без read-back и повторять вызов с теми же аргументами.
- Создавать дубль фильтра при повторном применении: `create_view_filter` переиспользует
  фильтр с тем же именем и категориями (`reused=true`).
";

    /// <summary>Skill markdown of a pack, or null when the pack has no skill.</summary>
    public static string? TryGet(string pack) =>
        string.Equals(pack, "filters", StringComparison.Ordinal) ? Filters : null;

    public static bool TryGet(string pack, out string? skill)
    {
        skill = TryGet(pack);
        return skill is not null;
    }

    public static IReadOnlyList<string> PacksWithSkill =>
        new[] { "filters" };
}