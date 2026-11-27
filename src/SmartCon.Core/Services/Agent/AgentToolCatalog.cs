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
            ["list_system_types"] = new("Read", false, new[]
            {
                "системные типы", "типы стен", "тип стены", "system types", "wall types",
                "типы воздуховодов", "типы перекрытий", "WallType", "DuctType", "FloorType"
            }),
            ["list_mep_systems"] = new("Read", false, new[]
            {
                "системы", "система", "mep", "инженерные системы", "трубопровод", "трубы",
                "воздуховоды", "электрические цепи", "аббревиатура", "systems", "piping",
                "mechanical", "electrical", "circuits", "MEPSystem"
            }),
            ["list_materials"] = new("Read", false, new[]
            {
                "материалы", "материал", "materials", "material", "материалов", "цвет",
                "прозрачность", "блеск", "класс материала"
            }),
            ["get_selected_elements"] = new("Read", false, new[]
            {
                "выделение", "выбрано", "выбранные", "selection", "selected"
            }),
            ["open_model_background"] = new("Read", false, new[]
            {
                "открыть модель", "эталон", "в фоне", "скрытно", "донор", "мульти-модельность",
                "open background", "посмотреть в другой модели"
            }),
            ["close_model"] = new("Read", false, new[]
            {
                "закрыть модель", "закрыть эталон", "close model", "закрыть донор"
            }),
            ["list_known_models"] = new("Read", false, new[]
            {
                "известные модели", "реестр моделей", "в какой модели", "где делали",
                "known models", "список моделей"
            }),
            ["get_document_status"] = new("Read", false, new[]
            {
                "статус документа", "только для чтения", "read-only", "можно ли писать",
                "можно ли изменять", "редактор семейства", "открытые документы",
                "совместный доступ", "document status", "writable", "permission denied",
                "документ заблокирован"
            }),
            ["get_titleblock_options"] = new("Read", false, new[]
            {
                "титульный блок", "основная надпись", "штамп", "формат листа", "параметр размера",
                "title block", "параметры титульника", "следующий номер листа"
            }),
            ["get_view_placement_preview"] = new("Read", false, new[]
            {
                "размер вида", "бумажный размер", "влезет на лист", "габариты видового окна",
                "кроп", "paper size", "формат под вид", "сколько места займёт вид"
            }),
            ["build_system_sheets"] = new("Write", true, new[]
            {
                "собрать листы", "сборщик листов", "компоновка листа", "листы под виды",
                "вид плюс экспликация", "выпустить листы", "листы для 3d-видов",
                "сформировать альбом"
            }),
            ["create_floor_plans"] = new("Write", true, new[]
            {
                "планы этажей", "пакет планов", "план на каждом уровне", "по образцу",
                "планы по уровням", "шаблон на планы", "floor plans", "создать планы"
            }),
            ["create_views_batch"] = new("Write", true, new[]
            {
                "пакет видов", "несколько видов", "виды по списку", "пачка видов",
                "3d-виды на системы", "массово виды", "batch views", "создать виды пачкой"
            }),
            ["rename_schedule"] = new("Write", false, new[]
            {
                "переименовать спецификацию", "архив", "архивная", "пометка архива",
                "смени имя ведомости", "rename schedule", "имя спецификации"
            }),
            ["set_schedule_field_format"] = new("Write", false, new[]
            {
                "заголовок колонки", "ширина колонки", "заголовки", "формат колонок",
                "width", "heading", "оформление колонок"
            }),
            ["set_schedule_style"] = new("Write", false, new[]
            {
                "стиль спецификации", "полосатые строки", "зебра", "текст таблицы",
                "striped rows", "стиль таблицы", "оформление таблицы"
            }),
            ["remove_schedule_from_sheet"] = new("Write", false, new[]
            {
                "снять с листа", "убрать вынос", "убрать с листа", "unplace",
                "снять ведомость", "снять спецификацию"
            }),
            ["clone_schedule"] = new("Write", false, new[]
            {
                "клонировать спецификацию", "пересобрать ведомость", "аналог спецификации",
                "фоп заказчика", "перевод на фоп", "перемаппить поля", "clone schedule"
            }),
            ["semantic_match"] = new("Read", false, new[]
            {
                "семантический", "похожий", "подобрать", "выбор", "топ", "классификатор", "фоп",
                "semantic", "match", "похоже", "аналог"
            }),
            ["read_classifier"] = new("Read", false, new[]
            {
                "классификатор", "файл", "txt", "коды", "иерархия", "раздел", "крошки",
                "classifier", "прочитать", "разобрать"
            }),
            ["list_classification_entries"] = new("Read", false, new[]
            {
                "классификатор", "классификация", "код по классификатору", "таблица классификации",
                "коды", "assembly code", "classification", "classification table"
            }),
            ["load_classification_table"] = new("Write", false, new[]
            {
                "классификатор", "классификация", "загрузить классификатор", "подключить классификатор",
                "таблица классификации", "classification table", "classification settings"
            }),
            ["classify_types"] = new("Read", true, new[]
            {
                "заполнить коды классификатора", "проставь коды по классификатору",
                "подбери коды для всех семейств", "классификация модели",
                "присвой коды классификатора", "автоматическая классификация",
                "код по классификатору всем типам", "классифицируй типы",
                "проставь коды классификатора по таблице", "заполни assembly code",
                "fill classification codes", "classify types", "assign assembly codes",
                "suggest classification codes"
            }),
            ["get_element_relations"] = new("Read", false, new[]
            {
                "связи", "соединён", "коннектор", "сосед", "хост", "стена", "помещение",
                "relations", "connectors", "host", "joined"
            }),
            ["trace_system_chain"] = new("Read", false, new[]
            {
                "цепь", "система", "разрыв", "обрыв", "трасса", "ветка", "цепочка", "gap",
                "chain", "break", "trace"
            }),
            ["get_room_contents"] = new("Read", false, new[]
            {
                "помещение", "состав", "комната", "что стоит", "содержимое", "офис",
                "room", "contents", "состав помещения"
            }),
            ["group_families_by_shared_parameters"] = new("Read", false, new[]
            {
                "группировка", "семейства", "фоп", "общие параметры", "сигнатура", "группы",
                "наборы", "guid", "grouping", "signature", "compact", "без общих параметров",
                "groupBy prefix", "префикс параметра", "сколько ФОП", "источник параметров"
            }),
            ["audit_fill_rate"] = new("Read", false, new[]
            {
                "аудит", "заполненность", "пустые", "сводка", "виновники", "незаполненные",
                "audit", "fill", "empty", "проверка модели"
            }),
            ["list_type_parameter_candidates"] = new("Read", false, new[]
            {
                "типоразмер", "параметры типа", "группировка идентификация", "свободные",
                "пустые системные", "кандидаты", "type", "candidates", "identity"
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
            ["get_element_bounds"] = new("Read", false, new[]
            {
                "координаты", "координаты элемента", "точка вставки", "габариты",
                "габаритный бокс", "bounding box", "bbox", "позиция элемента", "где стоит",
                "расстояние", "element bounds", "центр элемента", "размеры элемента"
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
            ["duplicate_view"] = new("Write", false, new[]
            {
                "дублировать вид", "копия вида", "duplicate view", "copy view",
                "зависимый вид", "dependent view", "клон вида"
            }),
            ["create_view"] = new("Write", false, new[]
            {
                "создать вид", "новый вид", "create view", "новый план", "новый разрез",
                "создай 3d", "новая легенда", "create plan", "create section", "create 3d"
            }),
            ["set_browser_folder"] = new("Write", true, new[]
            {
                "папка браузера", "подпапка", "browser folder", "sub-folder", "разложить виды",
                "переместить вид в папку", "sub-directory", "браузер проекта"
            }),
            ["rename_levels"] = new("Write", true, new[]
            {
                "переименовать", "уровень", "этаж", "rename", "level name"
            }),
            ["create_level"] = new("Write", false, new[]
            {
                "создать уровень", "новый уровень", "новый этаж", "добавить уровень",
                "create level", "new level", "add floor", "новая отметка"
            }),
            ["create_view_template"] = new("Write", false, new[]
            {
                "создать шаблон вида", "новый шаблон", "create view template",
                "шаблон из вида", "шаблон на тип системы", "шаблон без фильтров"
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
            ["copy_crop_from_view"] = new("Write", true, new[]
            {
                "подрезка", "обрезка", "crop", "как эталон", "рамка", "вырезать", "copy crop",
                "область подрезки"
            }),
            ["list_scope_boxes"] = new("Read", false, new[]
            {
                "габаритный бокс", "масштабный бокс", "scope box", "объём интереса",
                "границы вида", "scopebox"
            }),
            ["assign_scope_box"] = new("Write", true, new[]
            {
                "габаритный бокс", "масштабный бокс", "scope box", "назначить бокс",
                "привязать вид к бокс", "scopebox", "снять бокс"
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
            ["list_project_parameters"] = new("Read", false, new[]
            {
                "проектные параметры", "параметры проекта", "привязка параметра", "биндинг",
                "project parameters", "parameter bindings", "какие параметры в проекте",
                "забинден", "привязать параметр категорию"
            }),
            ["add_project_shared_parameter"] = new("Write", false, new[]
            {
                "создать общий параметр в проекте", "добавить проектный параметр",
                "привязать общий параметр", "забиндить параметр на категорию",
                "add project shared parameter", "bind shared parameter", "новый параметр проекта"
            }),
            ["rebind_project_parameter"] = new("Write", false, new[]
            {
                "сменить тип привязки", "изменить binding параметра", "rebind параметра",
                "параметр экземпляра вместо типа", "типовая привязка", "instance binding",
                "type binding", "перепривязать параметр", "сменить группу параметра",
                "перенести параметр в группу", "parameter group", "PG_DATA"
            }),
            ["remove_project_parameter"] = new("Write", false, new[]
            {
                "удалить проектный параметр", "убрать привязку параметра", "unbind параметра",
                "remove project parameter", "отвязать параметр", "удалить привязку фоп",
                "удалить определение общего параметра из документа"
            }),
            ["set_shared_parameter_file"] = new("Write", false, new[]
            {
                "файл общих параметров", "подключить ФОП", "создать ФОП", "настроить ФОП",
                "общие параметры", "ФОП", "нет ФОП", "путь к ФОП", "shared parameter file",
                "set shared parameter file", "shared_parameter_file_missing"
            }),
            ["undo_last"] = new("Write", false, new[]
            {
                "отменить", "откатить", "вернуть", "откат", "undo", "revert", "rollback"
            }),
            ["undo_until"] = new("Write", false, new[]
            {
                "откатить до", "откатить несколько", "откатить всё после", "вернуть к состоянию",
                "undo until", "revert chain", "откатить серию", "откатить все изменения до"
            }),
            ["list_change_sets"] = new("Read", false, new[]
            {
                "наборы изменений", "история изменений", "журнал изменений", "change sets",
                "какие изменения", "что менял агент", "list change sets", "список наборов"
            }),
            ["get_change_set_details"] = new("Read", false, new[]
            {
                "детали набора изменений", "что входило в набор", "состав набора изменений",
                "change set details", "что изменил набор", "элементы набора изменений"
            }),
            ["save_document"] = new("Write", false, new[]
            {
                "сохранить документ", "сохранить проект", "сохранить файл", "сохранить модель",
                "save document", "сохранить", "записать на диск", "save"
            }),
            ["audit_model"] = new("Read", false, new[]
            {
                "аудит модели", "проверить модель", "проблемы модели", "найти проблемы",
                "audit model", "здоровье модели", "проверка модели", "чекап модели"
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
            ["get_schedule_elements"] = new("Read", false, new[]
            {
                "элементы ведомости", "элементы спецификации", "id строк спецификации",
                "какие элементы в ведомости", "schedule elements", "element ids of schedule",
                "типы в спецификации", "состав спецификации"
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
            ["move_viewports"] = new("Write", true, new[]
            {
                "пакетно переместить видовые окна", "сдвинуть все виды", "массовый сдвиг",
                "move viewports", "сдвиг сетки видов", "выровнять видовые окна пакетно"
            }),
            ["remove_viewports"] = new("Write", true, new[]
            {
                "снять вид с листа", "убрать виды с листа", "снять размещение вида",
                "remove viewports", "unplace view", "освободить лист", "удалить вьюпорты"
            }),
            ["set_sheet_number"] = new("Write", false, new[]
            {
                "номер листа", "сменить номер листа", "перенумеровать лист",
                "set sheet number", "renumber sheet", "присвоить номер"
            }),
            ["renumber_sheets"] = new("Write", true, new[]
            {
                "перенумеровать листы", "массовая перенумерация", "последовательные номера",
                "renumber sheets", "сдвинуть номера листов", "вставить номер в середину",
                "сквозная нумерация", "пронумеровать блок листов"
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
            ["get_family_parameter_values"] = new("Read", false, new[]
            {
                "значения параметра по типам", "формула параметра", "read-back параметра",
                "verify formula", "parameter values per type", "сверить значения"
            }),
            ["get_family_labels"] = new("Read", false, new[]
            {
                "лейблы семейства", "надписи марки", "labels", "label", "лейбл",
                "семейства марок", "tag family labels", "аннотационные семейства",
                "марки", "проверить марки", "titleblock labels"
            }),
            ["get_parameter_usage"] = new("Read", false, new[]
            {
                "использование параметра", "где используется параметр", "parameter usage",
                "какие спецификации используют параметр", "какие фильтры используют",
                "обратный индекс параметров", "матрица влияния параметра",
                "список ведомостей по параметру", "фильтры по guid", "usage audit"
            }),
            ["find_families_with_parameter"] = new("Read", false, new[]
            {
                "найти семейства с параметром", "find families with parameter",
                "в каких семействах", "Наименование_ГОСТ", "matchedParameter",
                "instance параметр", "параметр экземпляра", "параметр по ГОСТ",
                "PREFIX_", "BP_", "BIM_", "ФОП", "shared parameters", "общие параметры",
                "префикс параметра", "prefix", "аудит параметров", "инвентаризация семейств",
                "миграция параметров", "families by prefix", "fields", "проекция полей",
                "разбивка по категориям", "byCategory"
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
                "продолжить результат", "stored result", "проекция", "project", "projection",
                "выбрать поля из результата", "data.groups[0]"
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
            ["delete_filter"] = new("Write", true, new[]
            {
                "удалить фильтр", "убрать фильтр", "delete filter", "remove filter",
                "удалить мёртвые фильтры", "почистить фильтры партией"
            }),
            ["merge_duplicate_filters"] = new("Write", false, new[]
            {
                "объединить дубликаты фильтров", "слить дубликаты фильтров",
                "merge filters", "замена дубликата фильтра"
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
            ["remove_schedule_field"] = new("Write", false, new[]
            {
                "удалить поле спецификации", "убрать колонку ведомости",
                "remove schedule field", "remove column", "убрать поле", "delete field"
            }),
            ["set_schedule_field_order"] = new("Write", false, new[]
            {
                "порядок полей спецификации", "порядок колонок ведомости",
                "переставить колонки", "field order", "column order", "reorder fields",
                "переместить колонку"
            }),
            ["set_schedule_field_visibility"] = new("Write", false, new[]
            {
                "скрыть колонку ведомости", "показать поле спецификации", "скрыть поле",
                "hide schedule field", "show column", "hide column", "спрятать колонку"
            }),
            ["set_schedule_totals"] = new("Write", false, new[]
            {
                "итоги спецификации", "итоговая строка ведомости", "сумма колонки",
                "итого", "totals", "grand total", "посчитать сумму в ведомости"
            }),
            ["move_schedule_on_sheet"] = new("Write", false, new[]
            {
                "переместить спецификацию на листе", "сдвинуть ведомость на листе",
                "передвинуть таблицу", "move schedule on sheet", "move schedule",
                "позиция спецификации"
            }),
            ["set_mep_system_type"] = new("Write", false, new[]
            {
                "аббревиатура системы", "аббревиатура типа системы",
                "переименовать тип системы", "сокращение системы", "set mep system type",
                "аббревиатура инженерной системы", "поменять аббревиатуру"
            }),
            ["get_browser_organization"] = new("Read", false, new[]
            {
                "организация браузера", "структура браузера проекта", "группировка видов",
                "browser organization", "как организован браузер", "уровни папок браузера",
                "текущая организация браузера"
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
            ["duplicate_family_type"] = new("Write", false, new[]
            {
                "дублировать тип", "копия типоразмера", "duplicate family type",
                "duplicate type", "копировать типоразмер", "новый тип на основе"
            }),
            ["rename_family_type"] = new("Write", false, new[]
            {
                "переименовать типоразмер", "rename family type", "переименовать тип семейства",
                "новое имя типа", "смена имени типоразмера"
            }),
            ["replace_family_type"] = new("Write", true, new[]
            {
                "заменить типоразмер", "replace family type", "сменить тип у элементов",
                "перевести на другой тип", "переключить тип", "bulk замена типа"
            }),
            ["load_family"] = new("Write", false, new[]
            {
                "загрузить семейство", "load family", "загрузка rfa", "подгрузить семейство",
                "добавить семейство из файла", "load rfa"
            }),
            ["purge_families"] = new("Write", true, new[]
            {
                "очистить семейства", "purge families", "неиспользуемые семейства",
                "удалить неиспользуемые семейства", "почистить проект от семейств",
                "семейства без экземпляров", "purge unused"
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
                "replace across families", "пакетная замена", "массовое переименование",
                "переименовать все", "переименовывание", "массовая правка"
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
