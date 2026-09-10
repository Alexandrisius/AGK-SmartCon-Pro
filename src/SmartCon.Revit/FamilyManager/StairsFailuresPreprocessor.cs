using Autodesk.Revit.DB;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// <see cref="IFailuresPreprocessor"/> для <c>StairsEditScope.Commit</c>
/// (ADR-027 Phase 2): предупреждения геометрии лестницы (автокоррекция числа
/// подступенков/глубины проступи под тип) принимаются как есть — staged
/// мини-проект является промежуточным хранилищем эталона и не имеет
/// интерактивного Undo-контекста.
/// </summary>
internal sealed class StairsFailuresPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        => FailureProcessingResult.Continue;
}
