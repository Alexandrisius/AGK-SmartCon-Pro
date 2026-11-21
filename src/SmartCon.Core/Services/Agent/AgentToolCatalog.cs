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
                "детали", "параметры", "элемент", "details", "parameters", "element",
                "прочитать", "read", "тип", "семейство", "typeId", "familyId", "essence"
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
            ["set_view_template_inclusion"] = new("Write", true, new[]
            {
                "шаблон", "не контролирует", "открепить", "отвязать", "include", "in template",
                "detach", "включить", "исключить", "template inclusion", "include in template"
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
                "заменить", "ремап", "remap", "replace value", "переименовать значение",
                "bulk elements", "many elements", "сотни элементов", "массовая замена"
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
            ["create_sheet"] = new("Write", false, new[]
            {
                "создать лист", "новый лист", "добавить лист", "create sheet", "new sheet",
                "номер нового листа"
            }),
            ["create_viewports"] = new("Write", true, new[]
            {
                "разместить виды на листе", "разложить по листам", "поставить вид на лист",
                "create viewports", "place views", "компоновка листа", "видовые окна пакетно"
            }),
            ["move_viewport"] = new("Write", false, new[]
            {
                "переместить видовое окно", "сдвинуть вид на листе", "move viewport",
                "позиция видового окна", "передвинуть вьюпорт"
            }),
            ["set_sheet_number"] = new("Write", false, new[]
            {
                "номер листа", "сменить номер листа", "перенумеровать лист",
                "set sheet number", "renumber sheet", "присвоить номер"
            }),
            ["rename_sheet"] = new("Write", false, new[]
            {
                "переименовать лист", "имя листа", "название листа", "rename sheet",
                "назови лист", "смена названия"
            }),
            ["sanitize_sheet_numbers"] = new("Write", true, new[]
            {
                "очистить номера листов", "невидимые символы", "sanitize sheet numbers",
                "почистить номера", "исправить номера листов"
            }),
            ["create_view_filter"] = new("Write", true, new[]
            {
                "фильтр вида", "создать фильтр", "скрыть по параметру", "убрать с вида",
                "фильтр по системе", "view filter", "create filter", "фильтр по уровню"
            }),
            ["set_filter_overrides"] = new("Write", true, new[]
            {
                "покрасить", "цвет фильтра", "overrides", "заливка фильтра", "штриховка фильтра",
                "прозрачность фильтра", "halftone", "графические замены"
            }),
            ["list_families"] = new("Read", false, new[]
            {
                "семейства", "семейство", "families", "family", "загруженные семейства",
                "список семейств", "типы семейств"
            }),
            ["list_family_types"] = new("Read", false, new[]
            {
                "типы", "типоразмеры", "типы семейства", "family types", "types",
                "неразмещённые типы", "typeId"
            }),
            ["get_family_parameters"] = new("Read", false, new[]
            {
                "параметры семейства", "family parameters", "общие параметры", "shared parameters",
                "GUID параметра", "формула параметра", "значения по типам"
            }),
            ["find_families_with_parameter"] = new("Read", false, new[]
            {
                "найти семейства с параметром", "find families with parameter",
                "в каких семействах", "ТДСК_Наименование", "АДСК_Наименование", "matchedParameter",
                "instance параметр", "параметр экземпляра", "ADSK_Наименование"
            }),
            ["rename_family_parameter"] = new("Write", false, new[]
            {
                "переименовать параметр", "rename parameter", "имя параметра",
                "переименование параметра семейства", "rename family parameter"
            }),
            ["add_family_parameter"] = new("Write", false, new[]
            {
                "добавить параметр", "создать параметр", "add parameter", "общий параметр",
                "shared parameter", "параметр экземпляра", "параметр типа"
            }),
            ["set_family_parameter_values"] = new("Write", true, new[]
            {
                "значения по типам", "задать значение параметра", "set family parameter values",
                "значение параметра типа", "единицы мм", "проставить значения"
            }),
            ["set_family_formula"] = new("Write", false, new[]
            {
                "формула", "formula", "назначить формулу", "снять формулу", "size_lookup",
                "lookup table", "вычисляемый параметр"
            }),
            ["map_family_parameters"] = new("Write", true, new[]
            {
                "перенести значения", "map parameters", "map family parameters", "смапить",
                "перенос параметров семейства", "скопировать формулу", "source target",
                "bulk families", "many families", "миграция семейств", "migration"
            }),
            ["find_tools"] = new("Meta", false, new[]
            {
                "найти инструмент", "какой инструмент", "find tool", "tool search"
            }),
            ["read_tool_result"] = new("Meta", false, new[]
            {
                "сохранённый результат", "полный результат", "resulthandle", "дочитать",
                "продолжить результат", "stored result"
            }),
            ["list_filters"] = new("Read", false, new[]
            {
                "фильтры видов", "список фильтров", "view filters", "list filters",
                "где применён фильтр"
            }),
            ["list_filterable_parameters"] = new("Read", false, new[]
            {
                "фильтруемые параметры", "совместимость параметра", "filterable parameters",
                "можно ли фильтровать", "параметр и категория", "preflight фильтра"
            }),
            ["get_filter_details"] = new("Read", false, new[]
            {
                "правила фильтра", "filter details", "filter rules", "категории фильтра",
                "из чего состоит фильтр"
            }),
            ["set_filter_rules"] = new("Write", false, new[]
            {
                "изменить фильтр", "правила фильтра", "set filter rules", "очистить фильтр",
                "настроить фильтр"
            }),
            ["delete_filter"] = new("Write", false, new[]
            {
                "удалить фильтр", "убрать фильтр", "delete filter", "remove filter"
            }),
            ["delete_views"] = new("Write", true, new[]
            {
                "удалить вид", "убрать вид", "delete view", "remove view", "почистить виды"
            }),
            ["delete_sheets"] = new("Write", true, new[]
            {
                "удалить лист", "убрать лист", "delete sheet", "remove sheet", "почистить листы"
            }),
            ["delete_elements"] = new("Write", true, new[]
            {
                "удалить элементы", "удалить элемент", "delete element", "remove element",
                "удалить стену", "удалить объекты", "почистить элементы"
            }),
            ["delete_schedules"] = new("Write", true, new[]
            {
                "удалить спецификацию", "удалить ведомость", "убрать спецификацию",
                "delete schedule", "remove schedule", "почистить ведомости"
            }),
            ["create_schedule"] = new("Write", false, new[]
            {
                "создать спецификацию", "новая ведомость", "сделать спецификацию", "create schedule",
                "таблица по категории", "ведомость труб", "спецификация стен"
            }),
            ["add_schedule_field"] = new("Write", false, new[]
            {
                "добавить поле спецификации", "добавить колонку ведомости", "дополнить спецификацию",
                "add schedule field", "add column", "вписать поле", "новая колонка"
            }),
            ["set_schedule_filter"] = new("Write", false, new[]
            {
                "фильтр спецификации", "настроить фильтр ведомости", "отфильтровать спецификацию",
                "schedule filter", "условие отбора", "ограничить строки ведомости"
            }),
            ["set_schedule_sort"] = new("Write", false, new[]
            {
                "сортировка спецификации", "отсортировать ведомость", "группировка ведомости",
                "schedule sort", "порядок строк", "каждый элемент строкой", "по возрастанию"
            }),
            ["place_schedule_on_sheet"] = new("Write", false, new[]
            {
                "разместить спецификацию на листе", "положить ведомость на лист",
                "поместить таблицу на лист", "place schedule on sheet", "вставить ведомость"
            }),
            ["delete_family_types"] = new("Write", true, new[]
            {
                "удалить типоразмер", "удалить тип", "delete family type", "remove type"
            }),
            ["remove_family_parameter"] = new("Write", false, new[]
            {
                "удалить параметр семейства", "remove family parameter", "убрать параметр",
                "почистить параметры"
            }),
            ["replace_family_parameter"] = new("Write", false, new[]
            {
                "заменить общий параметр", "replace family parameter", "перепривязать параметр",
                "rebind shared parameter", "сменить общий параметр", "подставить другой общий"
            }),
            ["set_view_parameters"] = new("Write", true, new[]
            {
                "параметры вида", "set view parameters", "назначение вида", "раздел",
                "папка браузера", "имя вида в штампе"
            }),
            ["rename_filter"] = new("Write", false, new[]
            {
                "переименовать фильтр", "rename filter", "имя фильтра", "название фильтра",
                "переименование фильтра"
            }),
            ["get_schedule_definition"] = new("Read", false, new[]
            {
                "фильтры спецификации", "определение спецификации", "schedule definition",
                "почему спецификация пустая", "поля спецификации", "сортировка спецификации"
            }),
            ["list_shared_parameters"] = new("Read", false, new[]
            {
                "общие параметры", "ФОП", "shared parameters", "активный ФОП",
                "файл общих параметров", "определения параметров"
            }),
            ["start_job"] = new("Write", true, new[]
            {
                "массово", "пакетно", "batch", "bulk", "bulk job", "job", "задание", "движок",
                "все элементы", "сотни", "по правилу", "массовая операция", "прогон",
                "миграция", "migration", "migrate", "массовая миграция", "заменить во всех",
                "всех семействах", "сотни семейств", "many families", "hundreds",
                "replace across families", "пакетная замена"
            }),
            ["get_job_status"] = new("Read", true, new[]
            {
                "статус", "прогресс", "progress", "status", "готово ли", "сколько осталось",
                "состояние задания", "wait", "подождать", "poll", "опрос", "job status"
            }),
            ["cancel_job"] = new("Write", true, new[]
            {
                "отмена", "остановить", "прервать", "cancel", "stop", "abort", "стоп",
                "завершить задание", "прекратить", "не продолжать"
            }),
            ["get_job_report"] = new("Read", true, new[]
            {
                "отчёт", "отчет", "report", "diff", "что изменилось", "csv", "проверка",
                "верификация", "verified", "mismatch", "расхождения", "результат задания"
            }),
            ["rollback_job"] = new("Write", true, new[]
            {
                "откат", "откатить", "rollback", "вернуть как было", "undo job", "восстановить",
                "обратно", "отменить задание", "restore", "реверс"
            }),
            ["retry_failed_items"] = new("Write", true, new[]
            {
                "повтор", "повторить", "retry", "доделать", "перезапустить", "failed",
                "ошибки задания", "упавшие элементы", "продолжить", "retry failed"
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
            RequiredContext = card?.RequiredContext,
            BatchAnalogue = card?.BatchAnalogue
        };
    }
}
