namespace SmartCon.Core.Models.Agent.Eval;

/// <summary>
/// The 40-task Russian eval catalog (night run 2, phase A). Exactly 15 read
/// tasks (including the three regression cases from the tester report), 15
/// write dry-run tasks (including the reference-view and unsupported cases) and
/// 10 apply+undo tasks.
/// </summary>
public static class AgentEvalTasks
{
    public static IReadOnlyList<AgentEvalTask> All { get; } = new List<AgentEvalTask>
    {
        // ── Read: 12 pure + 3 regression cases ──────────────────────────────
        new()
        {
            Id = "read_overview",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Что за проект? Кратко: сколько уровней, видов и категорий.",
            ExpectedTools = new[] { "get_project_overview" },
            ExpectedDataKeys = new[] { "levelCount", "viewCount" }
        },
        new()
        {
            Id = "read_levels",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Перечисли все уровни проекта с отметками в миллиметрах.",
            ExpectedTools = new[] { "list_levels" },
            ExpectedDataKeys = new[] { "total", "levels" }
        },
        new()
        {
            Id = "read_plans_count",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Сколько в проекте видов планов этажей?",
            ExpectedTools = new[] { "list_views" },
            ExpectedDataKeys = new[] { "total" }
        },
        new()
        {
            Id = "read_filtered_views",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Покажи виды, в имени которых есть SC_Eval, не больше 10.",
            ExpectedTools = new[] { "list_views" },
            ExpectedDataKeys = new[] { "views" }
        },
        new()
        {
            Id = "read_templates",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Какие шаблоны видов есть в проекте?",
            ExpectedTools = new[] { "list_view_templates" },
            ExpectedDataKeys = new[] { "total", "templates" }
        },
        new()
        {
            Id = "read_view_settings",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Покажи настройки вида {refViewId}.",
            ExpectedTools = new[] { "get_view_settings" },
            ExpectedDataKeys = new[] { "viewId", "hasTemplate", "scale" }
        },
        new()
        {
            Id = "read_view_fields",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Покажи только масштаб и view range вида {refViewId}.",
            ExpectedTools = new[] { "get_view_settings" },
            ExpectedDataKeys = new[] { "scale", "viewRange" }
        },
        new()
        {
            Id = "read_template_details",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Что контролирует шаблон {templateId} и какие у него настройки?",
            ExpectedTools = new[] { "get_view_template_details" },
            ExpectedDataKeys = new[] { "template", "settings" }
        },
        new()
        {
            Id = "read_wall_count",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Сколько стен в модели?",
            ExpectedTools = new[] { "summarize_elements" },
            ExpectedDataKeys = new[] { "total", "groups" }
        },
        new()
        {
            Id = "read_walls_by_comment",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Найди стены с комментарием, содержащим SC_Eval_Wall.",
            ExpectedTools = new[] { "query_elements" },
            ExpectedDataKeys = new[] { "total", "elements" }
        },
        new()
        {
            Id = "read_element_details",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Дай детали элемента {wallId}: только непустые параметры, без параметров типа.",
            ExpectedTools = new[] { "get_element_details" },
            ExpectedDataKeys = new[] { "total", "elements" }
        },
        new()
        {
            Id = "read_current_view",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Что сейчас открыто (активный вид) и какой у него масштаб?",
            ExpectedTools = new[] { "get_current_view_info" },
            ExpectedDataKeys = new[] { "name", "scale" },
            AllowErrorCodes = new[] { "no_active_view" },
            Notes = "The integration test host has no UI session, so an honest " +
                    "no_active_view is an acceptable outcome."
        },
        new()
        {
            Id = "read_sheets",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Сколько листов в проекте? Перечисли номера и названия.",
            ExpectedTools = new[] { "list_sheets" },
            ExpectedDataKeys = new[] { "total", "sheets" }
        },
        new()
        {
            Id = "read_sheet_content",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Что размещено на листе SC-EVAL-101? Назови виды и их координаты.",
            ExpectedTools = new[] { "get_sheet_content" },
            ExpectedDataKeys = new[] { "sheet", "views" },
            Notes = "Requires a title block type in the project template or the " +
                    "Revit content library."
        },
        new()
        {
            Id = "read_schedules",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Какие ведомости есть в проекте? Покажи их названия и категории.",
            ExpectedTools = new[] { "list_schedules" },
            ExpectedDataKeys = new[] { "total", "schedules" }
        },
        new()
        {
            Id = "read_schedule_data",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Покажи первые строки ведомости SC_Eval_Schedule.",
            ExpectedTools = new[] { "get_schedule_data" },
            ExpectedDataKeys = new[] { "columns", "rows" }
        },

        // Regression cases from the tester report.
        new()
        {
            Id = "reg_levels_consistent",
            Kind = AgentEvalTaskKind.Read,
            Prompt = "Сколько уровней в проекте? Сверь ответ list_levels с общим обзором проекта.",
            ExpectedTools = new[] { "list_levels", "get_project_overview" },
            ExpectedDataKeys = new[] { "total", "levelCount" },
            Check = AgentEvalCheck.LevelsCountConsistent,
            Notes = "C1: list_levels returned 1 of 7 levels in the tester report."
        },
        new()
        {
            Id = "reg_type_name_contains",
            Kind = AgentEvalTaskKind.Read,
            Prompt =
                "Найди стены, у которых параметр «Тип» содержит «{typeName}». " +
                "Используй условие по параметру ELEM_TYPE_PARAM.",
            ExpectedTools = new[] { "query_elements" },
            ExpectedDataKeys = new[] { "total", "elements" },
            Check = AgentEvalCheck.TypeNameContains,
            Notes = "C2: ElementId parameters compared via empty AsValueString."
        },
        new()
        {
            Id = "reg_compact_view_settings",
            Kind = AgentEvalTaskKind.Read,
            Prompt =
                "Дай краткую сводку настроек вида {refViewId}: шаблон, масштаб и число " +
                "скрытых категорий. Полный список категорий не нужен.",
            ExpectedTools = new[] { "get_view_settings" },
            ExpectedDataKeys = new[] { "hiddenCategoryCount", "scale" },
            Check = AgentEvalCheck.CompactViewSettings,
            Notes = "C4: compact payload by default."
        },

        // ── Write dry-run: 13 pure + reference-view + unsupported ───────────
        new()
        {
            Id = "dry_rename_view",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt = "Переименуй вид {targetAId} в SC_Eval_Renamed_A. Только dry-run, не применяй.",
            ExpectedTools = new[] { "rename_views" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "dry_rename_views_batch",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Переименуй виды {targetAId}, {targetBId}, {targetCId} в SC_Eval_P1, SC_Eval_P2, " +
                "SC_Eval_P3. Только dry-run, не применяй.",
            ExpectedTools = new[] { "rename_views" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 3
        },
        new()
        {
            Id = "dry_rename_level",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt = "Переименуй уровень {level2Id} в SC_Eval_L2_Renamed. Только dry-run, не применяй.",
            ExpectedTools = new[] { "rename_levels" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "dry_apply_template",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt = "Примени шаблон {templateId} к виду {targetAId}. Только dry-run, не применяй.",
            ExpectedTools = new[] { "apply_view_template" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "dry_apply_template_batch",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Примени шаблон {templateId} к видам {targetAId}, {targetBId}, {targetCId}. " +
                "Только dry-run, не применяй.",
            ExpectedTools = new[] { "apply_view_template" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 3
        },
        new()
        {
            Id = "dry_view_range",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Поставь виду {targetAId} view range относительно уровня {levelId}: top +3000 мм, " +
                "cut plane +1200 мм, bottom 0 мм, view depth -1000 мм. Только dry-run, не применяй.",
            ExpectedTools = new[] { "set_view_range" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "dry_crop_active",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt = "Включи область подрезки (crop region) у вида {targetAId}. Только dry-run, не применяй.",
            ExpectedTools = new[] { "set_view_crop" },
            ExpectedDataKeys = new[] { "changed", "dryRun" }
        },
        new()
        {
            Id = "dry_crop_extents",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Задай виду {targetAId} прямоугольную область подрезки от (-5000, -5000) до " +
                "(5000, 5000) мм и включи её. Только dry-run, не применяй.",
            ExpectedTools = new[] { "set_view_crop" },
            ExpectedDataKeys = new[] { "changed", "dryRun" }
        },
        new()
        {
            Id = "dry_visibility",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt = "Скрой категорию OST_Furniture у вида {targetAId}. Только dry-run, не применяй.",
            ExpectedTools = new[] { "set_view_visibility" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "dry_configure_view",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Настрой вид {targetBId}: переименуй в SC_Eval_Configured, примени шаблон " +
                "{templateId} и включи crop. Только dry-run, не применяй.",
            ExpectedTools = new[] { "configure_views" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "dry_copy_settings",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Скопируй настройки вида {refViewId} на вид {targetBId}, все секции. " +
                "Только dry-run, не применяй.",
            ExpectedTools = new[] { "copy_view_settings" },
            ExpectedDataKeys = new[] { "changed", "dryRun" }
        },
        new()
        {
            Id = "dry_set_parameter",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Поставь комментарий SC_Eval_Updated всем стенам, у которых комментарий содержит " +
                "SC_Eval_Wall. Только dry-run, не применяй.",
            ExpectedTools = new[] { "query_elements", "set_parameters" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 3
        },
        new()
        {
            Id = "dry_remap_parameter",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Замени значение параметра «Комментарии» с SC_Eval_Wall на SC_Eval_Remapped " +
                "у всех стен категории OST_Walls. Только dry-run, не применяй.",
            ExpectedTools = new[] { "remap_parameter_values" },
            AlternativeTools = new[] { "set_parameters" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            ExpectedChanged = 3
        },
        new()
        {
            Id = "dry_reference_view",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Сделай виды {targetAId}, {targetBId}, {targetCId} как эталон {refViewId}: " +
                "те же настройки и имена по образцу SC_Eval_Ref_1, SC_Eval_Ref_2, SC_Eval_Ref_3. " +
                "Только dry-run, не применяй.",
            ExpectedTools = new[] { "copy_view_settings" },
            AlternativeTools = new[] { "configure_views" },
            ExpectedDataKeys = new[] { "changed", "dryRun" },
            Check = AgentEvalCheck.ReferenceViewPlan,
            Notes = "A1: a template does not carry the name — the plan must cover the rename."
        },
        new()
        {
            Id = "dry_unsupported",
            Kind = AgentEvalTaskKind.WriteDryRun,
            Prompt =
                "Создай общий параметр «ФОП_Этаж» и назначь его всем стенам, а потом прогони " +
                "это по 2000 элементам разом.",
            ExpectedTools = Array.Empty<string>(),
            Check = AgentEvalCheck.HonestUnsupported,
            Notes = "No shared-parameter creation or bulk job tool exists — refuse honestly."
        },

        // ── Apply + undo: 10 real writes, each reverted and verified ─────────
        new()
        {
            Id = "apply_rename_view",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt = "Переименуй вид {targetAId} в SC_Eval_Applied_A.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "rename_views" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "apply_rename_level",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt = "Переименуй уровень {level2Id} в SC_Eval_L2_Applied.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "rename_levels" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "apply_template",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt = "Примени шаблон {templateId} к виду {targetAId}.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "apply_view_template" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "apply_view_range",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt =
                "Поставь виду {targetAId} view range относительно уровня {levelId}: top +3000 мм, " +
                "cut plane +1200 мм, bottom 0 мм, view depth -1000 мм.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "set_view_range" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "apply_crop_active",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt = "Включи область подрезки (crop region) у вида {targetAId}.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "set_view_crop" }
        },
        new()
        {
            Id = "apply_crop_extents",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt =
                "Задай виду {targetAId} прямоугольную область подрезки от (-5000, -5000) до " +
                "(5000, 5000) мм и включи её.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "set_view_crop" }
        },
        new()
        {
            Id = "apply_visibility",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt = "Скрой категорию OST_Furniture у вида {targetBId}.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "set_view_visibility" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "apply_configure_view",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt =
                "Настрой вид {targetBId}: переименуй в SC_Eval_Configured и примени шаблон " +
                "{templateId}.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "configure_views" },
            ExpectedChanged = 1
        },
        new()
        {
            Id = "apply_copy_settings",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt = "Скопируй настройки вида {refViewId} на вид {targetCId}, все секции.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "copy_view_settings" }
        },
        new()
        {
            Id = "apply_set_parameter",
            Kind = AgentEvalTaskKind.WriteApplyUndo,
            Prompt =
                "Поставь комментарий SC_Eval_Applied всем стенам, у которых комментарий содержит " +
                "SC_Eval_Wall.",
            ApplyPrompt = "Да, применяй.",
            ExpectedTools = new[] { "query_elements", "set_parameters" },
            ExpectedChanged = 3
        }
    };

    public static int CountByKind(AgentEvalTaskKind kind) =>
        All.Count(task => task.Kind == kind);
}
