namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> MepSkills() =>
        new[] { ("mep", Mep) };

    private const string Mep = @"## Пак mep: MEP-системы и материалы

### Карта тулов
- `list_mep_systems` — инженерные системы: `nameContains`, `domain`
  (piping|mechanical|electrical), `classification`, пагинация; карточка
  `systemId/name/domain/classification/typeId/abbreviation/elementCount/equipmentCount`.
- `list_materials` — материалы: `nameContains`, пагинация; поля
  `materialId/name/materialClass/category/color/transparency/shininess`.
- `set_mep_system_type` — аббревиатура/имя типа системы (`typeId|systemId` + `abbreviation`/`name`).

### Кухня Revit
- Система ≠ тип: аббревиатура/классификация живут на типе (`typeId`) как свойства; у
  электрических цепей типа нет → null. Аббревиатура на инстансе — read-only зеркало:
  `set_parameters` откажет (readonly_parameter), пишет тип `set_mep_system_type`.
- `elementCount` — терминальные элементы, оборудование отдельно (`equipmentCount` 0/1);
  пустая система (isEmpty) валидна.
- Материалы — обычные элементы: `set_parameters` по `materialId`
  (например `MATERIAL_PARAM_TRANSPARENCY`), детали — `get_element_details`.
- `list_materials` НЕ считает использование (это скан всей модели): где применён
  материал — `query_elements` с условием по параметру материала.

### Рецепты
1. «Какие системы в модели»: `list_mep_systems` → элементы через `query_elements`.
2. «Смени аббревиатуру»: `set_mep_system_type(typeId|systemId, abbreviation)` — свойство
   типа, все системы типа отразят сразу; undo_last вернёт.
3. «Материалы»: `list_materials` → правки через `set_parameters(materialId…)`.

### Что нельзя
- Создавать системы/материалы или менять состав системы.
- Ждать classification/аббревиатуру у электрических цепей.
";
}
