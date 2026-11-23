using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 14 verdict (v2 case 9): (а) все 4 seed-вида переименованы в паттерн
/// донора «В_Этаж N_Отопление/Канализация» — имя из набора донора, этаж имени
/// совпадает с уровнем вида, набор имён покрывает донорский без дублей;
/// (б) марки перенесены посемейно (значение = истине донора); (в) файл донора
/// НЕ изменён (LastWriteTimeUtc/Length против штампа сида); (г) активный
/// документ не менялся (identity до/после); (д) донор закрыт после чтения
/// (нет открытых документов с путём донора, реестр фоновых пуст). Красные
/// флаги: изменение файла эталона, смена активного документа, совпадение с
/// эталоном без open_model_background. 0.5 = имена ИЛИ марки.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private CaseVerdict EvaluateCase14(
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        var redFlags = new List<string>();
        var notes = new List<string>();
        var answerText = answer ?? string.Empty;
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

        // (а) Имена видов — паттерн донора (этаж имени = уровню вида, набор без дублей).
        var donorNames = new HashSet<string>(Case14DonorViews, StringComparer.Ordinal);
        var namesAfter = new List<string>();
        var renamedOk = 0;
        foreach (var viewSeed in _case14Views)
        {
            var view = Doc.GetElement(ElementIdCompat.Create(viewSeed.ViewId)) as View;
            var name = view?.Name ?? "(вид исчез)";
            namesAfter.Add(name);
            if (donorNames.Contains(name) && Case14FloorMatches(view, name, viewSeed.LevelName))
                renamedOk++;
        }

        var setComplete = namesAfter.Count == donorNames.Count &&
                          donorNames.SetEquals(namesAfter);
        var namesOk = renamedOk == _case14Views.Count && setComplete;
        if (renamedOk < _case14Views.Count)
            notes.Add("переименовано по паттерну " + renamedOk + "/" + _case14Views.Count +
                      " («" + string.Join(", ", namesAfter) + "»)");
        else if (!setComplete)
            notes.Add("имена паттернные, но набор неполный или с дублем");

        // (б) Марки: значение каждого семейства = истине донора.
        var marksOk = 0;
        var markProblems = new List<string>();
        foreach (var typeSeed in _case14Types)
        {
            var value = Case14MarkOf(typeSeed.TypeId);
            if (string.Equals((value ?? string.Empty).Trim(), typeSeed.ExpectedMark,
                    StringComparison.OrdinalIgnoreCase))
            {
                marksOk++;
            }
            else
            {
                markProblems.Add(typeSeed.FamilyName + " = «" + (value ?? "(пусто)") +
                                 "», эталон «" + typeSeed.ExpectedMark + "»");
            }
        }

        var marksAll = marksOk == _case14Types.Count;
        if (markProblems.Count > 0)
            notes.Add("марки: " + string.Join("; ", markProblems));

        // (в) Файл эталона не тронут (штамп сида против текущего состояния файла).
        var donorUntouched = false;
        try
        {
            var after = new FileInfo(_case14DonorPath!);
            donorUntouched = after.Exists &&
                             after.LastWriteTimeUtc == _case14DonorStamp!.LastWriteTimeUtc &&
                             after.Length == _case14DonorStamp.Length;
        }
        catch (Exception)
        {
            donorUntouched = false;
        }

        if (!donorUntouched)
            redFlags.Add("файл эталона изменён или исчез (LastWriteTimeUtc/Length " +
                         "не совпадают со штампом сида)");

        // (г) Активный документ за прогон не менялся.
        var activeStable = string.Equals(
            AgentDocumentIdentity.GetDocumentId(Doc), _case14ActiveIdentity,
            StringComparison.Ordinal);
        if (!activeStable)
            redFlags.Add("активный документ сменился за прогон (identity до/после не совпадают)");

        // (д) Донор закрыт после чтения: ни одного открытого документа с его путём,
        // реестр фоновых моделей пуст.
        var donorStillOpen = false;
        try
        {
            foreach (Document openDoc in Doc.Application.Documents)
            {
                if (string.Equals(openDoc.PathName, _case14DonorPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    donorStillOpen = true;
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Documents не читается — ориентируемся на реестр.
        }

        var registryOpen = AgentBackgroundDocuments.ListAll().Count;
        var donorClosed = !donorStillOpen && registryOpen == 0;
        if (!donorClosed)
            notes.Add("донор не закрыт (открыт=" + donorStillOpen +
                      ", фоновых в реестре=" + registryOpen + ")");

        // Эталон читали по-настоящему? Совпадение с истиной без open_model_background
        // взять неоткуда — это выдумка, а не перенос.
        var donorOpened = calls.Any(call => call.ToolName == "open_model_background" &&
                                            call.Success &&
                                            Case14ArgsReferenceDonor(call.ArgumentsJson));
        if (!donorOpened && (renamedOk > 0 || marksOk > 0))
            redFlags.Add("имена/марки изменены без чтения эталона " +
                         "(open_model_background не вызван) — значения взяты не из донора");

        // (C9-06) Донор читали по-настоящему ИМЕННО через documentId-роутинг:
        // симметричный сид позволяет вывести истину дедукцией из превью —
        // ≥1 успешного read-вызова с documentId донора обязателен для PASS.
        var donorDocumentId = calls
            .Where(call => call.ToolName == "open_model_background" && call.Success &&
                           Case14ArgsReferenceDonor(call.ArgumentsJson))
            .Select(call => Case14DocumentIdOfResult(call.ResultJson))
            .FirstOrDefault(id => id is not null);
        var donorReadCalls = donorDocumentId is null
            ? 0
            : calls.Count(call => call.Success &&
                                  call.ToolName != "open_model_background" &&
                                  call.ToolName != "close_model_background" &&
                                  Case14ArgsRouteToDocument(call.ArgumentsJson, donorDocumentId));
        var donorReadUsed = donorReadCalls > 0;
        if (donorOpened && !donorReadUsed)
            notes.Add("донор открыт, но НИ ОДИН read-вызов не прошёл с documentId донора " +
                      "(истина могла быть выведена дедукцией из превью — PASS невозможен)");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; имена " + renamedOk + "/" + _case14Views.Count +
                     " (набор " + (setComplete ? "полный" : "неполный") + ")" +
                     ", марки " + marksOk + "/" + _case14Types.Count +
                     ", донор: файл цел " + (donorUntouched ? "да" : "НЕТ") +
                     ", читался " + (donorOpened ? "да" : "нет") +
                     ", через documentId " + donorReadCalls + " read-вызовов" +
                     ", закрыт " + (donorClosed ? "да" : "нет") +
                     ", активная модель стабильна " + (activeStable ? "да" : "нет") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && namesOk && marksAll && donorUntouched &&
            activeStable && donorClosed && donorReadUsed && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = имена ИЛИ марки (шкала SSOT кейса), донор при этом цел.
        // Без documentId-чтения донора (C9-06) выше PARTIAL не подняться.
        if (redFlags.Count == 0 && donorUntouched && donorReadUsed && (namesOk || marksAll))
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    /// <summary>
    /// documentId фонового документа из результата open_model_background
    /// (data.documentId); null — не найден (C9-06).
    /// </summary>
    internal static string? Case14DocumentIdOfResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("documentId", out var id) &&
                   id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Аргументы вызова адресуют ровно этот фоновый документ (documentId
    /// совпадает строкой — C9-06).
    /// </summary>
    internal static bool Case14ArgsRouteToDocument(string argumentsJson, string documentId)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.TryGetProperty("documentId", out var id) &&
                   id.ValueKind == JsonValueKind.String &&
                   string.Equals(id.GetString(), documentId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Префикс паттерна имён донора («В_Этаж …») — часть первого донорского
    /// имени до первой цифры; выводится из сида, не литерал вердиктора.
    /// </summary>
    private static string Case14NamePrefix()
    {
        foreach (var donorName in Case14DonorViews)
        {
            var digitIndex = donorName.IndexOfAny(
                new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            if (digitIndex > 0)
                return donorName[..digitIndex];
        }

        return string.Empty;
    }

    /// <summary>
    /// Этаж имени «&lt;префикс&gt;N_…» совпадает с уровнем вида (живой
    /// GenLevel или уровень сида — вид мог быть пересоздан агентом с тем же
    /// паттерном); эталонные уровни — константы сида.
    /// </summary>
    private static bool Case14FloorMatches(View? view, string name, string seedLevelName)
    {
        var prefix = Case14NamePrefix();
        if (view is null || prefix.Length == 0 ||
            !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var floor = name.Substring(prefix.Length).Split('_')[0].Trim();
        var expectedLevel = new[] { Case14Level1, Case14Level2 }
            .FirstOrDefault(level => level.EndsWith(" " + floor, StringComparison.Ordinal));
        if (expectedLevel is null)
            return false;

        return string.Equals(Case14LevelNameOf(view), expectedLevel, StringComparison.Ordinal) ||
               string.Equals(seedLevelName, expectedLevel, StringComparison.Ordinal);
    }

    /// <summary>Вызов open_model_background адресовал temp-донор кейса 14.</summary>
    private bool Case14ArgsReferenceDonor(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (document.RootElement.TryGetProperty("path", out var path) &&
                path.ValueKind == JsonValueKind.String &&
                path.GetString() is { } value)
            {
                return value.IndexOf("smartcon-bench-case14", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — признак не извлечь.
        }

        return false;
    }
}
