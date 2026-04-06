namespace SmartCon.Core.Math.FormulaEngine.Ast;

/// <summary>
/// AST-узел для size_lookup(tableName, targetParam, defaultValue, queryParam1, queryParam2, ...).
/// 
/// TODO [Фаза 6B — Multi-Column LookupTable]:
/// Сейчас SizeLookupNode хранит только метаданные для ParseSizeLookup.
/// В будущем модуле мультиколоночного парсинга таблиц поиска этот узел будет
/// использоваться для построения полного маппинга:
///   - Каждый FamilyParameter с формулой size_lookup → один SizeLookupNode
///   - Собрать ВСЕ SizeLookupNode семейства → маппинг столбцов на параметры
///   - Маппинг параметров на коннекторы → комбинаторная фильтрация строк CSV
/// См. план: ILookupTableService.GetAllRows / FilterRows / GetNearestValidCombination
/// </summary>
internal sealed record SizeLookupNode(
    string TableName,
    string TargetParameter,
    string DefaultValue,
    IReadOnlyList<string> QueryParameters) : AstNode;
