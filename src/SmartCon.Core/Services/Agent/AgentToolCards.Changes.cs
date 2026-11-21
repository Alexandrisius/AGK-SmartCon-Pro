using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Cards of the G.10 change-set surface: listing and inspecting the change sets
/// of the active document, reverting a whole chain down to a given set, saving
/// the document and the read-only model audit.
/// </summary>
public static partial class AgentToolCards
{
    private static AgentToolCard[] ChangesCards() =>
        new[]
        {
            new AgentToolCard
            {
                ToolName = "list_change_sets",
                Pack = "changes",
                Purpose =
                    "Список наборов изменений SmartCon текущего документа (новые сверху): " +
                    "changeSetId, операция, время, число элементов, статус " +
                    "applied/rolledBack/discarded.",
                WhenToUse =
                    "Когда нужно узнать, что агент менял в документе, найти changeSetId для " +
                    "get_change_set_details / undo_until или понять, что ещё можно откатить.",
                WhenNotToUse =
                    "Когда нужен состав конкретного набора — get_change_set_details; когда нужен " +
                    "сразу откат — undo_last / undo_until без предварительного списка.",
                Examples = new[]
                {
                    "Какие изменения ты делал в этом документе?",
                    "Покажи историю изменений документа",
                    "Что можно ещё откатить?",
                    "Покажи последние наборы изменений со статусами"
                },
                Synonyms = new[]
                {
                    "наборы изменений", "история изменений", "журнал изменений", "change sets",
                    "список наборов изменений", "что менял агент", "list change sets",
                    "какие change set"
                },
                NegativeExamples = new[]
                {
                    "Откати последнее изменение",
                    "Покажи, что входило в набор изменений"
                },
                Idempotent = true,
                TimeoutMs = 30000,
                Bulk = false
            },
            new AgentToolCard
            {
                ToolName = "get_change_set_details",
                Pack = "changes",
                Purpose =
                    "Полная запись одного набора изменений: заголовок (операция, время, " +
                    "статус) и элементы — uniqueId, elementId, параметр, старое и новое " +
                    "значение, статус.",
                WhenToUse =
                    "Когда нужно объяснить пользователю, что именно изменил конкретный набор, " +
                    "или убедиться, что это тот набор, до которого стоит откатываться " +
                    "undo_until.",
                WhenNotToUse =
                    "Когда нужен просто список наборов — list_change_sets; когда нужно " +
                    "откатить — undo_last / undo_until.",
                Examples = new[]
                {
                    "Покажи, что входило в набор изменений <guid>",
                    "Что изменил набор <guid>?",
                    "Какие элементы менял этот change set?",
                    "Детали набора изменений перед откатом"
                },
                Synonyms = new[]
                {
                    "детали набора изменений", "состав набора изменений", "что входило в набор",
                    "change set details", "элементы набора изменений", "что изменил набор",
                    "запись набора", "старое новое значение"
                },
                NegativeExamples = new[]
                {
                    "Покажи список наборов изменений",
                    "Откати изменения"
                },
                Idempotent = true,
                TimeoutMs = 30000,
                Bulk = false
            },
            new AgentToolCard
            {
                ToolName = "undo_until",
                Pack = "changes",
                Purpose =
                    "Откат ВСЕХ applied наборов текущего документа от последнего вниз до " +
                    "указанного включительно, в обратном порядке — по одному undo_last на " +
                    "набор.",
                WhenToUse =
                    "Когда нужно отменить не одно последнее изменение, а серию до нужной точки: " +
                    "возьми changeSetId из list_change_sets, покажи dry-run и примени. Цепочка " +
                    "останавливается на первом конфликте; остальное остаётся applied.",
                WhenNotToUse =
                    "Когда нужно откатить только последний набор — undo_last; изменения из " +
                    "start_job — rollback_job; закрыть набор без отката — discard_change_set.",
                Examples = new[]
                {
                    "Откати все изменения после набора <guid>",
                    "Верни модель к состоянию до серии переименований",
                    "Откати всё, что ты сделал после правки труб",
                    "Сначала покажи dry-run отката до набора <guid>"
                },
                Synonyms = new[]
                {
                    "откатить до набора", "откатить несколько изменений", "откатить серию",
                    "откатить всё после", "вернуть к состоянию", "undo until",
                    "revert chain", "откатить цепочку", "отменить серию изменений"
                },
                NegativeExamples = new[]
                {
                    "Откати только последнее изменение",
                    "Откати задание из 531 элемента"
                },
                Idempotent = false,
                TimeoutMs = 300000,
                Bulk = false
            },
            new AgentToolCard
            {
                ToolName = "save_document",
                Pack = "changes",
                Purpose =
                    "Сохранение активного документа по текущему пути (аналог Ctrl+S). Не " +
                    "автосохранение; модель не меняет — change set не создаётся.",
                WhenToUse =
                    "Когда пользователь просит сохранить проект после подтверждённых правок. " +
                    "dryRun показывает IsModified, путь и число неоткаченных наборов " +
                    "изменений.",
                WhenNotToUse =
                    "Когда документ ни разу не сохранялся (нужен Save As в Revit — тул вернёт " +
                    "document_not_saved_before); когда просят «сохранить как» или в другой " +
                    "файл.",
                Examples = new[]
                {
                    "Сохрани документ",
                    "Сохрани проект после изменений",
                    "Проверь, есть ли несохранённые изменения",
                    "Покажи dry-run сохранения документа"
                },
                Synonyms = new[]
                {
                    "сохранить документ", "сохранить проект", "сохранить файл",
                    "сохранить модель", "save document", "записать на диск",
                    "несохранённые изменения", "сохрани"
                },
                NegativeExamples = new[]
                {
                    "Сохрани как новый файл",
                    "Откати последнее изменение"
                },
                Idempotent = true,
                TimeoutMs = 120000,
                Bulk = false
            },
            new AgentToolCard
            {
                ToolName = "audit_model",
                Pack = "changes",
                Purpose =
                    "Только чтение: 6 проверок здоровья модели — визуальные дубли номеров " +
                    "листов, невидимые символы в именах, виды без шаблона, пустые " +
                    "спецификации, неразмещённые семейства, неоткаченные наборы изменений.",
                WhenToUse =
                    "Когда пользователь просит «проверь модель», «найди проблемы» или перед " +
                    "сдачей проекта. Ничего не меняет: находки — подсказки для профильных " +
                    "тулов (sanitize_sheet_numbers, apply_view_template, purge_families...).",
                WhenNotToUse =
                    "Когда нужно сразу чинить: тул не меняет модель; когда нужен аудит одного " +
                    "типа объектов — профильные списки (list_sheets, list_schedules) точнее.",
                Examples = new[]
                {
                    "Проверь модель на типовые проблемы",
                    "Найди дубли номеров листов и невидимые символы",
                    "Какие спецификации пустые?",
                    "Аудит модели перед выдачей"
                },
                Synonyms = new[]
                {
                    "аудит модели", "проверить модель", "проблемы модели", "audit model",
                    "здоровье модели", "проверка проекта", "найти проблемы", "чекап модели"
                },
                NegativeExamples = new[]
                {
                    "Исправь найденные проблемы",
                    "Удали пустые спецификации"
                },
                Idempotent = true,
                TimeoutMs = 120000,
                Bulk = false
            }
        };
}
