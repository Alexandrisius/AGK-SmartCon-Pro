namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Human-readable tool labels for the UI. Labels follow the tool name literally
/// (list_views → "Список видов", get_view_settings → "Получить свойства вида"),
/// so the header always matches what the tool actually does.
/// </summary>
public static class AgentToolDisplayNames
{
    private static readonly Dictionary<string, string> Ru =
        new(StringComparer.Ordinal)
        {
            ["get_project_overview"] = "Получить сведения о проекте",
            ["list_views"] = "Список видов",
            ["list_levels"] = "Список уровней",
            ["list_categories"] = "Список категорий",
            ["get_selected_elements"] = "Получить выделенные элементы",
            ["query_elements"] = "Запросить элементы",
            ["get_element_details"] = "Получить сведения об элементах",
            ["summarize_elements"] = "Сводка элементов",
            ["list_view_templates"] = "Список шаблонов видов",
            ["get_view_settings"] = "Получить свойства вида",
            ["get_view_template_details"] = "Получить свойства шаблона вида",
            ["get_current_view_info"] = "Получить сведения об активном виде",
            ["list_sheets"] = "Список листов",
            ["get_sheet_content"] = "Получить содержимое листа",
            ["list_schedules"] = "Список спецификаций",
            ["get_schedule_data"] = "Получить данные спецификации",
            ["get_schedule_definition"] = "Получить свойства спецификации",
            ["list_families"] = "Список семейств",
            ["list_family_types"] = "Список типоразмеров семейств",
            ["get_family_parameters"] = "Получить параметры семейства",
            ["find_families_with_parameter"] = "Найти семейства с параметром",
            ["list_shared_parameters"] = "Список общих параметров",
            ["list_filters"] = "Список фильтров",
            ["get_filter_details"] = "Получить правила фильтра",
            ["rename_views"] = "Переименовать виды",
            ["rename_levels"] = "Переименовать уровни",
            ["apply_view_template"] = "Применить шаблон вида",
            ["set_view_template_inclusion"] = "Задать включение в шаблон вида",
            ["set_view_range"] = "Задать секущий диапазон",
            ["set_view_crop"] = "Задать подрезку вида",
            ["set_view_visibility"] = "Задать видимость вида",
            ["configure_views"] = "Настроить виды",
            ["copy_view_settings"] = "Скопировать настройки вида",
            ["create_view_filter"] = "Создать фильтр вида",
            ["set_filter_rules"] = "Задать правила фильтра",
            ["rename_filter"] = "Переименовать фильтр",
            ["delete_filter"] = "Удалить фильтр",
            ["set_view_parameters"] = "Задать параметры вида",
            ["delete_views"] = "Удалить виды",
            ["delete_sheets"] = "Удалить листы",
            ["delete_family_types"] = "Удалить типоразмеры семейств",
            ["remove_family_parameter"] = "Удалить параметр семейства",
            ["set_filter_overrides"] = "Задать переопределения фильтра",
            ["set_parameters"] = "Задать параметры",
            ["remap_parameter_values"] = "Переназначить значения параметров",
            ["rename_family_parameter"] = "Переименовать параметр семейства",
            ["add_family_parameter"] = "Добавить параметр семейства",
            ["set_family_parameter_values"] = "Задать значения параметров семейства",
            ["set_family_formula"] = "Задать формулу параметра",
            ["map_family_parameters"] = "Сопоставить параметры семейства",
            ["discard_change_set"] = "Отбросить набор изменений",
            ["undo_last"] = "Отменить последнее действие",
            ["find_tools"] = "Найти инструменты",
            ["load_tool_pack"] = "Загрузить пакет инструментов",
            ["read_tool_result"] = "Прочитать результат инструмента",
            ["execute_plan"] = "Выполнить план"
        };

    private static readonly Dictionary<string, string> En =
        new(StringComparer.Ordinal)
        {
            ["get_project_overview"] = "Get project information",
            ["list_views"] = "List views",
            ["list_levels"] = "List levels",
            ["list_categories"] = "List categories",
            ["get_selected_elements"] = "Get selected elements",
            ["query_elements"] = "Query elements",
            ["get_element_details"] = "Get element information",
            ["summarize_elements"] = "Element summary",
            ["list_view_templates"] = "List view templates",
            ["get_view_settings"] = "Get view properties",
            ["get_view_template_details"] = "Get view template properties",
            ["get_current_view_info"] = "Get current view info",
            ["list_sheets"] = "List sheets",
            ["get_sheet_content"] = "Get sheet content",
            ["list_schedules"] = "List schedules",
            ["get_schedule_data"] = "Get schedule data",
            ["get_schedule_definition"] = "Get schedule properties",
            ["list_families"] = "List families",
            ["list_family_types"] = "List family types",
            ["get_family_parameters"] = "Get family parameters",
            ["find_families_with_parameter"] = "Find families with parameter",
            ["list_shared_parameters"] = "List shared parameters",
            ["list_filters"] = "List filters",
            ["get_filter_details"] = "Get filter rules",
            ["rename_views"] = "Rename views",
            ["rename_levels"] = "Rename levels",
            ["apply_view_template"] = "Apply view template",
            ["set_view_template_inclusion"] = "Set view template inclusion",
            ["set_view_range"] = "Set view range",
            ["set_view_crop"] = "Set view crop",
            ["set_view_visibility"] = "Set view visibility",
            ["configure_views"] = "Configure views",
            ["copy_view_settings"] = "Copy view settings",
            ["create_view_filter"] = "Create view filter",
            ["set_filter_rules"] = "Set filter rules",
            ["rename_filter"] = "Rename filter",
            ["delete_filter"] = "Delete filter",
            ["set_view_parameters"] = "Set view parameters",
            ["delete_views"] = "Delete views",
            ["delete_sheets"] = "Delete sheets",
            ["delete_family_types"] = "Delete family types",
            ["remove_family_parameter"] = "Remove family parameter",
            ["set_filter_overrides"] = "Set filter overrides",
            ["set_parameters"] = "Set parameters",
            ["remap_parameter_values"] = "Remap parameter values",
            ["rename_family_parameter"] = "Rename family parameter",
            ["add_family_parameter"] = "Add family parameter",
            ["set_family_parameter_values"] = "Set family parameter values",
            ["set_family_formula"] = "Set parameter formula",
            ["map_family_parameters"] = "Map family parameters",
            ["discard_change_set"] = "Discard change set",
            ["undo_last"] = "Undo last action",
            ["find_tools"] = "Find tools",
            ["load_tool_pack"] = "Load tool pack",
            ["read_tool_result"] = "Read tool result",
            ["execute_plan"] = "Execute plan"
        };

    public static string Get(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return string.Empty;

        var names = LocalizationService.CurrentLanguage == Language.EN ? En : Ru;
        return names.TryGetValue(toolName!, out var name) ? name : Humanize(toolName!);
    }

    private static string Humanize(string toolName)
    {
        var text = toolName.Replace('_', ' ').Trim();
        return text.Length == 0
            ? toolName
            : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}