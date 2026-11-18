namespace SmartCon.Core.Services.Agent;

/// <summary>A named tool pack exposed by the catalog v2 router (runbook §7.2).</summary>
public sealed class AgentToolPack
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Tool packs for the catalog v2 router (runbook H1-07). Every existing tool
/// belongs to exactly one pack; packs without tools are reserved for later
/// quarters and keep their description so the catalog stays stable.
/// </summary>
public static class AgentToolPacks
{
    public const string ActiveView = "active_view";

    public const string Selection = "selection";

    private static readonly AgentToolPack[] Packs =
    {
        new()
        {
            Name = "views",
            Description = "Поиск, просмотр и переименование видов; активный вид.",
            Tools = new[] { "list_views", "get_current_view_info", "rename_views" }
        },
        new()
        {
            Name = "view_settings",
            Description = "Чтение и изменение настроек вида: диапазон, подрезка, видимость.",
            Tools = new[] { "get_view_settings", "set_view_range", "set_view_crop", "set_view_visibility" }
        },
        new()
        {
            Name = "view_edit",
            Description = "Композитная настройка видов и копирование настроек с эталона.",
            Tools = new[] { "configure_views", "copy_view_settings" }
        },
        new()
        {
            Name = "templates",
            Description = "Шаблоны видов: список, состав и назначение.",
            Tools = new[] { "list_view_templates", "get_view_template_details", "apply_view_template" }
        },
        new()
        {
            Name = "model",
            Description = "Обзор проекта, категории и текущее выделение.",
            Tools = new[] { "get_project_overview", "list_categories", "get_selected_elements" }
        },
        new()
        {
            Name = "levels",
            Description = "Уровни: список и переименование.",
            Tools = new[] { "list_levels", "rename_levels" }
        },
        new()
        {
            Name = "elements",
            Description = "Поиск, сводка и параметры элементов модели.",
            Tools = new[] { "query_elements", "summarize_elements", "get_element_details" }
        },
        new()
        {
            Name = "params",
            Description = "Запись и перенос значений параметров.",
            Tools = new[] { "set_parameters", "remap_parameter_values" }
        },
        new()
        {
            Name = "changes",
            Description = "Откат и закрытие наборов изменений SmartCon.",
            Tools = new[] { "undo_last", "discard_change_set" }
        },
        new()
        {
            Name = "plans",
            Description = "Многошаговые планы изменений.",
            Tools = new[] { "execute_plan" }
        },
        new()
        {
            Name = "meta",
            Description = "Поиск инструментов и чтение сохранённых результатов.",
            Tools = new[] { "find_tools", "read_tool_result" }
        },
        new()
        {
            Name = "sheets",
            Description = "Листы и размещение видов.",
            Tools = new[] { "list_sheets", "get_sheet_content" }
        },
        new()
        {
            Name = "families",
            Description = "Семейства и типы (задел следующих версий)."
        },
        new()
        {
            Name = "schedules",
            Description = "Ведомости и спецификации: список и данные.",
            Tools = new[] { "list_schedules", "get_schedule_data" }
        },
        new()
        {
            Name = "filters",
            Description = "Фильтры видов (задел следующих версий)."
        },
        new()
        {
            Name = "worksets",
            Description = "Рабочие наборы (задел следующих версий)."
        },
        new()
        {
            Name = "links",
            Description = "Связанные модели (задел следующих версий)."
        },
        new()
        {
            Name = "mep",
            Description = "Инженерные системы и коннекторы (задел следующих версий)."
        },
        new()
        {
            Name = "annotations",
            Description = "Аннотации и размеры (задел следующих версий)."
        },
        new()
        {
            Name = "audit",
            Description = "Проверки модели и отчёты (задел следующих версий)."
        }
    };

    // ADR-075 §2: core tools are pinned and never displaced by retrieval.
    private static readonly string[] CoreNames =
    {
        "get_project_overview",
        "list_views",
        "list_levels",
        "query_elements",
        "find_tools",
        "execute_plan",
        "rename_views",
        "apply_view_template"
    };

    private static readonly Dictionary<string, AgentToolPack> PacksByName = BuildPackIndex();
    private static readonly Dictionary<string, string> PackNameByTool = BuildToolIndex();
    private static readonly HashSet<string> CoreNameSet = new(CoreNames, StringComparer.Ordinal);

    /// <summary>All packs in the fixed catalog order (11 populated + 9 reserved).</summary>
    public static IReadOnlyList<AgentToolPack> All => Packs;

    /// <summary>Tools that are always available regardless of the loaded pack.</summary>
    public static IReadOnlyCollection<string> CoreToolNames => CoreNames;

    public static bool TryGet(string name, out AgentToolPack? pack) =>
        PacksByName.TryGetValue(name, out pack);

    /// <summary>Pack that owns the tool, or null when the tool is unknown.</summary>
    public static AgentToolPack? PackOfTool(string toolName) =>
        PackNameByTool.TryGetValue(toolName, out var packName) &&
        PacksByName.TryGetValue(packName, out var pack)
            ? pack
            : null;

    /// <summary>Tool names of a pack; empty when the pack is unknown or reserved.</summary>
    public static IReadOnlyList<string> ToolsInPack(string pack) =>
        PacksByName.TryGetValue(pack, out var found) ? found.Tools : Array.Empty<string>();

    public static bool IsCore(string name) => CoreNameSet.Contains(name);

    private static Dictionary<string, AgentToolPack> BuildPackIndex()
    {
        var index = new Dictionary<string, AgentToolPack>(Packs.Length, StringComparer.Ordinal);
        foreach (var pack in Packs)
            index.Add(pack.Name, pack);
        return index;
    }

    private static Dictionary<string, string> BuildToolIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in Packs)
        {
            foreach (var tool in pack.Tools)
                index.Add(tool, pack.Name);
        }

        return index;
    }
}
