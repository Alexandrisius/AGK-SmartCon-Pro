using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Cards of the elements pack that joined after <c>AgentToolCards.Model</c>
/// reached its size limit (#260): the placement and extents of model elements.
/// </summary>
public static partial class AgentToolCards
{
    private static AgentToolCard[] ElementsCards() =>
        new[]
        {
            new AgentToolCard
            {
                ToolName = "get_element_bounds",
                Pack = "elements",
                Purpose =
                    "Координаты и габариты элементов (до 50 id): точка вставки либо начало, " +
                    "конец, середина и длина кривой размещения (трубы, воздуховоды, стены, " +
                    "балки); габаритный параллелепипед (min, max, size, center) в координатах " +
                    "модели Revit, в мм — как координаты коннекторов get_element_relations и " +
                    "отметки list_levels. Типы — applicable=false (не размещены в модели).",
                WhenToUse =
                    "Когда нужно понять, где стоят элементы, как далеко они друг от друга и где " +
                    "разрыв системы; когда нужны координаты для отчёта или для сопоставления с " +
                    "коннекторами и отметками уровней.",
                WhenNotToUse =
                    "Когда нужны параметры элемента (get_element_details); когда нужно найти " +
                    "элементы в заданной точке — выборки по координатам нет, сначала " +
                    "query_elements; для типов семейств габаритов нет.",
                Examples = new[]
                {
                    "Покажи координаты этих труб",
                    "Где стоит элемент 12345?",
                    "Какие габариты у этого оборудования?",
                    "Насколько далеко друг от друга эти два элемента?",
                    "В какой точке разрыв между трубами?"
                },
                Synonyms = new[]
                {
                    "координаты", "координаты элемента", "точка вставки", "габариты",
                    "габаритный бокс", "bounding box", "bbox", "позиция элемента", "где стоит",
                    "расстояние между элементами", "element bounds", "центр элемента",
                    "размеры элемента"
                },
                NegativeExamples = new[]
                {
                    "Найди элементы в заданной точке (выборки по координатам нет — сначала query_elements)",
                    "Покажи параметры элемента (это get_element_details)"
                },
                Idempotent = true,
                TimeoutMs = 60000,
                Bulk = false
            }
        };
}
