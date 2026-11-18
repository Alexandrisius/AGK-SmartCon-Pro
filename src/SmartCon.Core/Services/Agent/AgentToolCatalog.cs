using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Routing metadata for the tool catalog (mvp-plan §20.4). Kept in one table so
/// the tool implementations stay focused on behaviour; AgentToolRegistry applies
/// the metadata to the definitions it serves to the LLM.
/// </summary>
public static class AgentToolCatalog
{
    private sealed record Metadata(string Category, bool Bulk, string[] Tags);

    private static readonly Dictionary<string, Metadata> MetadataByName =
        new(StringComparer.Ordinal)
        {
            ["get_project_overview"] = new("Read", false, new[]
            {
                "проект", "обзор", "сводка", "сколько", "итоги", "overview", "project", "summary"
            }),
            ["list_views"] = new("Read", false, new[]
            {
                "виды", "вид", "план", "разрез", "список", "перечень", "views", "list", "plans",
                "папка", "браузер", "организация", "folder", "browser", "organization"
            }),
            ["list_levels"] = new("Read", false, new[]
            {
                "уровни", "уровень", "отметки", "этажи", "levels", "elevation", "floors"
            }),
            ["list_categories"] = new("Read", false, new[]
            {
                "категории", "категория", "categories", "counts"
            }),
            ["get_selected_elements"] = new("Read", false, new[]
            {
                "выделение", "выбрано", "выбранные", "selection", "selected"
            }),
            ["query_elements"] = new("Read", false, new[]
            {
                "найти", "найди", "поиск", "фильтр", "элементы", "стены", "трубы", "без",
                "содержит", "query", "search", "filter", "contains", "notexists"
            }),
            ["get_element_details"] = new("Read", false, new[]
            {
                "детали", "параметры", "элемент", "details", "parameters", "element"
            }),
            ["summarize_elements"] = new("Read", false, new[]
            {
                "количество", "сколько", "сумма", "длина", "сводка", "группы",
                "summarize", "count", "sum", "group"
            }),
            ["list_view_templates"] = new("Read", false, new[]
            {
                "шаблоны", "шаблон", "templates", "view template"
            }),
            ["get_view_settings"] = new("Read", false, new[]
            {
                "настройки", "масштаб", "шаблон", "подрезка", "видимость", "view range",
                "settings", "scale", "crop", "visibility"
            }),
            ["get_view_template_details"] = new("Read", false, new[]
            {
                "шаблон", "контролирует", "настройки шаблона", "template", "details", "controlled"
            }),
            ["get_current_view_info"] = new("Read", false, new[]
            {
                "активный", "открыто", "текущий", "вид", "current", "active", "opened"
            }),
            ["rename_views"] = new("Write", true, new[]
            {
                "переименовать", "переименуй", "имя", "название", "rename", "view name"
            }),
            ["rename_levels"] = new("Write", true, new[]
            {
                "переименовать", "уровень", "этаж", "rename", "level name"
            }),
            ["apply_view_template"] = new("Write", true, new[]
            {
                "применить", "шаблон", "назначить", "template", "apply"
            }),
            ["set_view_range"] = new("Write", true, new[]
            {
                "view range", "диапазон", "отметки", "top", "cut plane", "bottom", "глубина"
            }),
            ["set_view_crop"] = new("Write", true, new[]
            {
                "подрезка", "область", "crop", "region", "рамка", "обрезать"
            }),
            ["set_view_visibility"] = new("Write", true, new[]
            {
                "скрыть", "показать", "видимость", "категория", "hide", "visibility", "show"
            }),
            ["configure_views"] = new("Write", true, new[]
            {
                "настроить", "настрой", "сконфигурировать", "configure", "batch"
            }),
            ["copy_view_settings"] = new("Write", true, new[]
            {
                "скопировать", "перенести", "как эталон", "как образец", "copy", "reference",
                "same as", "подобно"
            }),
            ["set_parameters"] = new("Write", true, new[]
            {
                "параметр", "параметры", "комментарий", "значение", "поставить", "установить",
                "parameter", "set", "value", "comments"
            }),
            ["remap_parameter_values"] = new("Write", true, new[]
            {
                "заменить", "ремап", "remap", "replace value", "переименовать значение"
            }),
            ["undo_last"] = new("Write", false, new[]
            {
                "отменить", "откатить", "вернуть", "откат", "undo", "revert", "rollback"
            }),
            ["discard_change_set"] = new("Write", false, new[]
            {
                "закрыть набор", "забыть изменение", "discard", "abandon", "change set"
            }),
            ["execute_plan"] = new("Write", true, new[]
            {
                "план", "сценарий", "несколько", "шагов", "сразу", "за один раз", "plan",
                "multi-step"
            }),
            ["list_schedules"] = new("Read", false, new[]
            {
                "ведомости", "ведомость", "спецификации", "спецификация", "schedules",
                "schedule list", "поля ведомости", "строки ведомости"
            }),
            ["get_schedule_data"] = new("Read", false, new[]
            {
                "данные ведомости", "строки спецификации", "содержимое ведомости",
                "schedule data", "таблица ведомости", "значения ведомости"
            }),
            ["list_sheets"] = new("Read", false, new[]
            {
                "листы", "лист", "комплект листов", "номера листов", "sheets", "sheet list",
                "чертежи", "альбом"
            }),
            ["get_sheet_content"] = new("Read", false, new[]
            {
                "содержимое листа", "что на листе", "виды на листе", "видовые окна",
                "координаты видов", "sheet content", "views on sheet"
            }),
            ["find_tools"] = new("Meta", false, new[]
            {
                "найти инструмент", "какой инструмент", "find tool", "tool search"
            }),
            ["read_tool_result"] = new("Meta", false, new[]
            {
                "сохранённый результат", "полный результат", "resulthandle", "дочитать",
                "продолжить результат", "stored result"
            })
        };

    public static AgentToolDefinition Enrich(AgentToolDefinition definition)
    {
        var hasMetadata = MetadataByName.TryGetValue(definition.Name, out var metadata);
        var hasCard = AgentToolCards.TryGet(definition.Name, out var card);
        if (!hasMetadata && !hasCard)
            return definition;

        return new AgentToolDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            Risk = definition.Risk,
            SupportsDryRun = definition.SupportsDryRun,
            InputSchemaJson = definition.InputSchemaJson,
            Category = hasMetadata && string.IsNullOrEmpty(definition.Category)
                ? metadata!.Category
                : definition.Category,
            Tags = hasMetadata && definition.Tags.Count == 0 ? metadata!.Tags : definition.Tags,
            SupportsBulk = hasMetadata ? definition.SupportsBulk || metadata!.Bulk : definition.SupportsBulk,
            Pack = card?.Pack ?? string.Empty,
            Purpose = card?.Purpose ?? string.Empty,
            WhenToUse = card?.WhenToUse ?? string.Empty,
            WhenNotToUse = card?.WhenNotToUse ?? string.Empty,
            Examples = card?.Examples ?? Array.Empty<string>(),
            Synonyms = card?.Synonyms ?? Array.Empty<string>(),
            NegativeExamples = card?.NegativeExamples ?? Array.Empty<string>(),
            Idempotent = card?.Idempotent ?? false,
            TimeoutMs = card?.TimeoutMs ?? 30000,
            RequiredContext = card?.RequiredContext
        };
    }
}
