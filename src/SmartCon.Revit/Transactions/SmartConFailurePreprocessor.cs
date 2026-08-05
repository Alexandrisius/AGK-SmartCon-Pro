using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;

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

            // Stress test 2026-08-05: every failure is logged BEFORE any
            // DeleteWarning — a swallowed warning hid the dependency chain
            // behind the "Циклическая взаимосвязь" dialog + silent rollback
            // (element id=2975 was never identifiable from the log).
            var failingIds = failure.GetFailingElementIds();
            var idText = failingIds is { Count: > 0 }
                ? string.Join(", ", failingIds.Select(id => id.GetValue().ToString()))
                : "(none)";
            if (severity == FailureSeverity.Warning)
            {
                SmartConLogger.Debug(
                    $"Revit failure (warning, suppressed): '{failure.GetDescriptionText()}' " +
                    $"failingElements=[{idText}]");
                failuresAccessor.DeleteWarning(failure);
            }
            else
            {
                SmartConLogger.Warn(
                    $"Revit failure ({severity}): '{failure.GetDescriptionText()}' " +
                    $"failingElements=[{idText}]. " +
                    "[Action: найдите элементы по id в проекте и устраните причину; при повторении — пришлите лог]");
            }
        }

        return FailureProcessingResult.Continue;
    }
}
