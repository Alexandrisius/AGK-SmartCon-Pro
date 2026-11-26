namespace SmartCon.Core.Services.Agent;

/// <summary>A named tool pack exposed by the catalog v2 router (runbook §7.2).</summary>
public sealed class AgentToolPack
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Tools loaded together with the pack but owned by another pack (for
    /// example get_view_settings is needed before the first filter step). They
    /// are enabled by load_tool_pack and never duplicate the pack ownership.
    /// </summary>
    public IReadOnlyList<string> Bootstrap { get; init; } = Array.Empty<string>();
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
            Description = "Поиск, просмотр, создание, дублирование, переименование, папки браузера и удаление видов; активный вид.",
            Tools = new[]
            {
                "list_views", "get_current_view_info", "rename_views", "delete_views",
                "duplicate_view", "create_view", "set_browser_folder", "get_browser_organization",
                "create_floor_plans", "create_views_batch"
            }
        },
        new()
        {
            Name = "view_settings",
            Description = "Чтение и изменение настроек вида: диапазон, подрезка, видимость, габаритные боксы, параметры.",
            Tools = new[]
            {
                "get_view_settings", "set_view_range", "set_view_crop", "set_view_visibility",
                "set_view_parameters", "list_scope_boxes", "assign_scope_box"
            }
        },
        new()
        {
            Name = "view_edit",
            Description = "Композитная настройка видов и копирование настроек с эталона.",
            Tools = new[] { "configure_views", "copy_view_settings", "copy_crop_from_view" }
        },
        new()
        {
            Name = "templates",
            Description = "Шаблоны видов: список, состав, назначение и Include in template.",
            Tools = new[]
            {
                "list_view_templates", "get_view_template_details", "apply_view_template",
                "set_view_template_inclusion", "create_view_template"
            }
        },
        new()
        {
            Name = "model",
            Description = "Обзор проекта, категории, системные типы, текущее выделение и подключение таблицы классификации к документу.",
            Tools = new[]
            {
                "get_project_overview", "list_categories", "list_system_types",
                "get_selected_elements", "load_classification_table"
            }
        },
        new()
        {
            Name = "levels",
            Description = "Уровни: список, создание и переименование.",
            Tools = new[] { "list_levels", "create_level", "rename_levels" }
        },
        new()
        {
            Name = "elements",
            Description = "Поиск, сводка, связи, координаты и габариты, аудит и удаление элементов.",
            Tools = new[]
            {
                "query_elements", "summarize_elements", "get_element_details", "get_element_bounds",
                "delete_elements", "group_families_by_shared_parameters", "audit_fill_rate",
                "list_type_parameter_candidates"
            }
        },
        new()
        {
            Name = "params",
            Description =
                "Запись и перенос значений параметров; проектные общие параметры; файл общих параметров (ФОП).",
            Tools = new[]
            {
                "set_parameters", "remap_parameter_values", "list_project_parameters",
                "add_project_shared_parameter", "set_shared_parameter_file"
            }
        },
        new()
        {
            Name = "changes",
            Description =
                "Наборы изменений SmartCon: список и детали, откат последнего и до указанного, закрытие набора; сохранение документа и аудит модели.",
            Tools = new[]
            {
                "list_change_sets", "get_change_set_details", "undo_last", "undo_until",
                "discard_change_set", "save_document", "audit_model"
            }
        },
        new()
        {
            Name = "jobs",
            Description = "Массовые операции движком заданий: старт, статус, отмена, отчёт, откат и повтор.",
            Tools = new[]
            {
                "start_job", "get_job_status", "cancel_job",
                "get_job_report", "rollback_job", "retry_failed_items"
            }
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
            Description = "Листы и размещение видов: список, содержимое, создание, размещение и снятие видовых окон, номера и имена.",
            Tools = new[]
            {
                "list_sheets", "get_sheet_content", "delete_sheets", "create_sheet",
                "create_viewports", "move_viewport", "move_viewports", "remove_viewports",
                "set_sheet_number", "renumber_sheets", "rename_sheet",
                "sanitize_sheet_numbers",
                "get_titleblock_options", "get_view_placement_preview", "build_system_sheets"
            }
        },
        new()
        {
            Name = "families",
            Description = "Семейства: типы, параметры, значения, формулы, перенос значений, загрузка и очистка.",
            Tools = new[]
            {
                "list_families", "list_family_types", "get_family_parameters",
                "find_families_with_parameter", "list_shared_parameters",
                "rename_family_parameter", "add_family_parameter",
                "set_family_parameter_values", "set_family_formula",
                "map_family_parameters", "delete_family_types", "remove_family_parameter",
                "replace_family_parameter", "duplicate_family_type", "rename_family_type",
                "replace_family_type", "load_family", "purge_families"
            }
        },
        new()
        {
            Name = "schedules",
            Description =
                "Ведомости и спецификации: список, данные, определение, создание, поля (добавление, удаление, порядок, видимость, итоги), фильтры, сортировка, размещение и перемещение на листе и удаление.",
            Tools = new[]
            {
                "list_schedules", "get_schedule_data", "get_schedule_definition",
                "get_schedule_elements", "delete_schedules",
                "create_schedule", "add_schedule_field", "remove_schedule_field",
                "set_schedule_field_order", "set_schedule_field_visibility", "set_schedule_totals",
                "set_schedule_filter", "set_schedule_sort", "place_schedule_on_sheet",
                "move_schedule_on_sheet", "rename_schedule", "set_schedule_field_format",
                "set_schedule_style", "remove_schedule_from_sheet", "clone_schedule"
            }
        },
        new()
        {
            Name = "filters",
            Description = "Фильтры видов: чтение, создание, правила, переименование, удаление (батч с карантином), слияние дубликатов и overrides.",
            Tools = new[]
            {
                "list_filters", "get_filter_details", "list_filterable_parameters",
                "create_view_filter", "set_filter_rules",
                "rename_filter", "delete_filter", "merge_duplicate_filters",
                "set_filter_overrides"
            },
            // #400: opening a filter on a view needs the view state first; loading
            // the pack must not force a second load_tool_pack for it.
            Bootstrap = new[] { "get_view_settings" }
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
            Description = "Инженерные системы и материалы: списки MEP-систем (трубы, воздуховоды, цепи) и материалов, свойства типов систем.",
            Tools = new[] { "list_mep_systems", "list_materials", "set_mep_system_type" }
        },
        new()
        {
            Name = "semantic",
            Description = "Семантический выбор топ-K из больших списков, парсинг иерархических txt-классификаторов с хлебными крошками, чтение таблицы классификации, загруженной в документ, и подбор кодов классификатора для типоразмеров одним вызовом.",
            Tools = new[] { "semantic_match", "read_classifier", "list_classification_entries", "classify_types" }
        },
        new()
        {
            Name = "relations",
            Description = "Связи модели: соединения элементов, хосты, from/to помещения, состав помещений, обход цепей систем и детект разрывов.",
            Tools = new[] { "get_element_relations", "trace_system_chain", "get_room_contents" }
        },
        new()
        {
            Name = "documents",
            Description = "Мульти-модельность: скрытное открытие модели-эталона в фоне, чтение через documentId, закрытие без сохранения, реестр моделей; статус документа.",
            Tools = new[] { "open_model_background", "close_model", "list_known_models", "get_document_status" }
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
    // The core is the RESEARCH set: context-gathering tools that must be callable
    // in the first round, before the model knows which packs it needs. Write
    // primitives live in their packs so they arrive together with the pack skill.
    private static readonly string[] CoreNames =
    {
        "get_project_overview",
        "list_categories",
        "list_levels",
        "list_views",
        "get_current_view_info",
        "get_view_settings",
        "query_elements",
        "summarize_elements",
        "get_element_details",
        "get_selected_elements",
        "find_tools"
    };

    // Harness plumbing, not research: always sent, never ranked, never counted as
    // the core. execute_plan orchestrates other tools and is referenced by the
    // static prompt, so it must be callable without a discovery round; its
    // detailed contract still ships in the plans pack skill.
    private static readonly string[] SystemNames =
    {
        "load_tool_pack",
        "read_tool_result",
        "execute_plan"
    };

    private static readonly Dictionary<string, AgentToolPack> PacksByName = BuildPackIndex();
    private static readonly Dictionary<string, string> PackNameByTool = BuildToolIndex();
    private static readonly HashSet<string> CoreNameSet = new(CoreNames, StringComparer.Ordinal);
    private static readonly HashSet<string> SystemNameSet = new(SystemNames, StringComparer.Ordinal);
    private static readonly AgentToolPack[] PopulatedPacks =
        Packs.Where(pack => pack.Tools.Count > 0).ToArray();

    /// <summary>All packs in the fixed catalog order (16 populated + 4 reserved).</summary>
    public static IReadOnlyList<AgentToolPack> All => Packs;

    /// <summary>
    /// Packs the model can actually load. Reserved packs are kept in the schema
    /// for forward compatibility but must not be advertised: listing empty
    /// packs looks like broken discovery (stress-test #7).
    /// </summary>
    public static IReadOnlyList<AgentToolPack> Populated => PopulatedPacks;

    /// <summary>Research core: context-gathering tools always available regardless of the loaded pack.</summary>
    public static IReadOnlyCollection<string> CoreToolNames => CoreNames;

    /// <summary>
    /// Harness plumbing that is always sent but is not part of the research core:
    /// the pack loader, the stored-result reader and the plan orchestrator.
    /// </summary>
    public static IReadOnlyCollection<string> SystemToolNames => SystemNames;

    public static bool TryGet(string name, out AgentToolPack? pack) =>
        PacksByName.TryGetValue(name, out pack);

    /// <summary>Pack that owns the tool, or null when the tool is unknown.</summary>
    public static AgentToolPack? PackOfTool(string toolName) =>
        PackNameByTool.TryGetValue(toolName, out var packName) &&
        PacksByName.TryGetValue(packName, out var pack)
            ? pack
            : null;

    /// <summary>Tool names of a pack; empty when the pack is unknown or reserved.</summary>
    public static IReadOnlyList<string> ToolsInPack(string pack)
    {
        if (!PacksByName.TryGetValue(pack, out var found))
            return Array.Empty<string>();

        if (found.Bootstrap.Count == 0)
            return found.Tools;

        var combined = new List<string>(found.Tools.Count + found.Bootstrap.Count);
        combined.AddRange(found.Tools);
        foreach (var name in found.Bootstrap)
        {
            if (!combined.Contains(name))
                combined.Add(name);
        }

        return combined;
    }

    public static bool IsCore(string name) => CoreNameSet.Contains(name);

    public const string LoadToolPackName = "load_tool_pack";

    public const string ReadToolResultName = "read_tool_result";

    /// <summary>
    /// Tools that every request carries regardless of routing and the loaded
    /// set: the research core plus the system plumbing (load_tool_pack,
    /// read_tool_result, execute_plan). They never compete for a request slot
    /// and never enter the working recency list.
    /// </summary>
    public static bool IsAlwaysAvailable(string name) =>
        CoreNameSet.Contains(name) || SystemNameSet.Contains(name);

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
