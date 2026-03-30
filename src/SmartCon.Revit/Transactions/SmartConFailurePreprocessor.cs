using Autodesk.Revit.DB;

namespace SmartCon.Revit.Transactions;

/// <summary>
/// Подавляет ожидаемые предупреждения при перемещении/повороте элементов (I-07).
/// Подключается к каждой Transaction через RevitTransactionService.
/// </summary>
public sealed class SmartConFailurePreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        var failures = failuresAccessor.GetFailureMessages();

        foreach (var failure in failures)
        {
            var severity = failure.GetSeverity();

            if (severity == FailureSeverity.Warning)
            {
                failuresAccessor.DeleteWarning(failure);
            }
        }

        return FailureProcessingResult.Continue;
    }
}
