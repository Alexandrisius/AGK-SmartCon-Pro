using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolCards
{
    private static AgentToolCard[] MepCards() =>
        new[]
        {
            new AgentToolCard
            {
                ToolName = "list_mep_systems",
                Pack = "mep",
                Purpose =
                    "Список инженерных систем (MEP): трубопроводные, воздуховодные и " +
                    "электрические цепи с доменом, классификацией, типом, аббревиатурой и " +
                    "количеством элементов.",
                WhenToUse =
                    "Когда нужно увидеть состав инженерных систем проекта, найти систему по " +
                    "имени, домену или классификации или взять идентификаторы системы и её " +
                    "типа для работы с параметрами.",
                WhenNotToUse =
                    "Когда нужны типы систем как элементы (Piping System Types) — " +
                    "list_system_types; когда нужны сами трубы/воздуховоды — query_elements.",
                Examples = new[]
                {
                    "Какие инженерные системы есть в проекте?",
                    "Покажи все трубопроводные системы и их аббревиатуры",
                    "Найди систему по имени «ХВС-1»",
                    "Сколько элементов в каждой системе воздуховодов?"
                },
                Synonyms = new[]
                {
                    "системы", "система", "инженерные системы", "меп", "mep", "трубопроводные",
                    "воздуховоды", "цепи", "MEPSystem", "piping system", "mechanical system",
                    "electrical system", "аббревиатура системы", "состав системы"
                },
                NegativeExamples = new[]
                {
                    "Покажи типы труб в проекте",
                    "Найди все трубы на уровне 1"
                },
                Idempotent = true,
                TimeoutMs = 30000,
                Bulk = false
            },
            new AgentToolCard
            {
                ToolName = "list_materials",
                Pack = "mep",
                Purpose =
                    "Список материалов проекта: имя, класс (Wood/Metal/Concrete), категория, " +
                    "цвет #RRGGBB, прозрачность и блеск.",
                WhenToUse =
                    "Когда нужен перечень материалов, их id для записи параметров или проверка " +
                    "свойств материала (цвет, прозрачность).",
                WhenNotToUse =
                    "Когда нужно найти, где материал используется — query_elements с условием " +
                    "по параметру материала; когда нужен расчёт объёмов — get_element_details.",
                Examples = new[]
                {
                    "Какие материалы есть в проекте?",
                    "Найди материал «Сталь» и покажи его цвет",
                    "Покажи классы всех материалов",
                    "Дай id материалов для записи параметров"
                },
                Synonyms = new[]
                {
                    "материалы", "материал", "materials", "material", "класс материала",
                    "цвет материала", "прозрачность", "блеск", "список материалов",
                    "материалов проекта", "Material"
                },
                NegativeExamples = new[]
                {
                    "Где используется материал «Бетон»?",
                    "Измените структуру стены"
                },
                Idempotent = true,
                TimeoutMs = 30000,
                Bulk = false
            }
        };
}
