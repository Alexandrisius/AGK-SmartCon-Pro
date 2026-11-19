namespace SmartCon.Core.Services.Agent;

public static class AgentToolDisplayNames
{
    private static readonly Dictionary<string, string> Names =
        new(StringComparer.Ordinal)
        {
            ["get_project_overview"] = "Сводка о проекте",
            ["list_views"] = "Список видов",
            ["list_levels"] = "Список уровней",
            ["list_categories"] = "Категории модели",
            ["get_selected_elements"] = "Выбранные элементы",
            ["query_elements"] = "Поиск элементов",
            ["get_element_details"] = "Чтение элементов",
            ["summarize_elements"] = "Сводка по элементам",
            ["list_view_templates"] = "Шаблоны видов",
            ["get_view_settings"] = "Настройки вида",
            ["get_view_template_details"] = "Настройки шаблона",
            ["get_current_view_info"] = "Активный вид",
            ["list_sheets"] = "Список листов",
            ["get_sheet_content"] = "Содержимое листа",
            ["list_schedules"] = "Список ведомостей",
            ["get_schedule_data"] = "Данные ведомости",
            ["get_schedule_definition"] = "Поля ведомости",
            ["list_families"] = "Загруженные семейства",
            ["list_family_types"] = "Типоразмеры семейства",
            ["get_family_parameters"] = "Параметры семейства",
            ["find_families_with_parameter"] = "Поиск семейств по параметру",
            ["list_shared_parameters"] = "Общие параметры",
            ["list_filters"] = "Фильтры вида",
            ["get_filter_details"] = "Правила фильтра",
            ["rename_views"] = "Переименование видов",
            ["rename_levels"] = "Переименование уровней",
            ["apply_view_template"] = "Назначение шаблона",
            ["set_view_template_inclusion"] = "Включение в шаблон",
            ["set_view_range"] = "Диапазон вида",
            ["set_view_crop"] = "Подрезка вида",
            ["set_view_visibility"] = "Видимость на виде",
            ["configure_views"] = "Настройка видов",
            ["copy_view_settings"] = "Копирование настроек вида",
            ["create_view_filter"] = "Создание фильтра",
            ["set_filter_rules"] = "Правила фильтра",
            ["rename_filter"] = "Переименование фильтра",
            ["delete_filter"] = "Удаление фильтра",
            ["set_view_parameters"] = "Параметры вида",
            ["delete_views"] = "Удаление видов",
            ["delete_sheets"] = "Удаление листов",
            ["delete_family_types"] = "Удаление типоразмеров",
            ["remove_family_parameter"] = "Удаление параметра",
            ["set_filter_overrides"] = "Графические замены",
            ["set_parameters"] = "Запись параметров",
            ["remap_parameter_values"] = "Перенос значений",
            ["rename_family_parameter"] = "Переименование параметра",
            ["add_family_parameter"] = "Добавление параметра",
            ["set_family_parameter_values"] = "Значения по типам",
            ["set_family_formula"] = "Формула параметра",
            ["map_family_parameters"] = "Сопоставление параметров",
            ["discard_change_set"] = "Закрыть набор изменений",
            ["undo_last"] = "Отмена изменений",
            ["find_tools"] = "Поиск инструментов",
            ["load_tool_pack"] = "Загрузка инструментов",
            ["read_tool_result"] = "Чтение результата",
            ["execute_plan"] = "Выполнение плана"
        };

    public static string Get(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return string.Empty;

        return Names.TryGetValue(toolName!, out var name) ? name : Humanize(toolName!);
    }

    private static string Humanize(string toolName)
    {
        var text = toolName.Replace('_', ' ').Trim();
        return text.Length == 0
            ? toolName
            : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}