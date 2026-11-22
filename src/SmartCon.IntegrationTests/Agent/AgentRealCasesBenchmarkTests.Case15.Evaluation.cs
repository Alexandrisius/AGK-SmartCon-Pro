using System.Globalization;
using System.Text.Json;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Verdict of case 15: six read-only questions graded against the seed truth
/// — valve connector neighbours (ids in the answer OR seen through
/// get_element_relations), the host wall of the window, from/to rooms of the
/// door, office contents by family with counts, the pump level, and the
/// break pair of the Т11/Т111 gap identified in the answer or inside
/// trace_system_chain result JSON plus the two-system explanation. Any
/// non-dryRun write call is a red flag and sinks the score.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private CaseVerdict EvaluateCase15(
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

        // Read-only кейс: write-тулов нет вообще (dryRun допустим).
        var writeTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "set_parameters", "set_view_parameters", "set_browser_folder", "rename_views",
            "set_family_parameter_values", "map_family_parameters", "replace_family_parameter",
            "create_view", "create_sheet", "start_job"
        };
        foreach (var call in calls)
        {
            if (writeTools.Contains(call.ToolName) && !IsDryRunCall(call))
            {
                redFlags.Add("read-only кейс: вызван write-тул " + call.ToolName + " с dryRun=false");
                break;
            }
        }

        // Соседи крана не изменились (сверка ConnectorManager с истиной сида).
        var neighbours = Case15ValveNeighbours();
        if (!neighbours.Contains(_case15.PipeAId) || !neighbours.Contains(_case15.PipeBId))
            redFlags.Add("соединения крана изменились после прогона: соседи [" +
                         string.Join(", ", neighbours) + "]");

        // Q1: соединения крана — два конкретных элемента.
        var q1 = Case15AnswerHasId(answerText, _case15.PipeAId) &&
                 Case15AnswerHasId(answerText, _case15.PipeBId);
        if (!q1)
        {
            q1 = Case15RelationsSaw(calls, _case15.ValveId,
                     new[] { _case15.PipeAId, _case15.PipeBId }) &&
                 ContainsText(answerText, "труб");
        }

        if (q1) notes.Add("Q1 соединения крана: да");
        else notes.Add("Q1 соединения крана: ответ не называет обе трубы крана");

        // Q2: окно — в какой стене.
        var q2 = Case15AnswerHasId(answerText, _case15.SouthWallId);
        if (!q2)
        {
            q2 = Case15RelationsSaw(calls, _case15.WindowId,
                     new[] { _case15.SouthWallId }) && ContainsText(answerText, "стен");
        }

        notes.Add("Q2 стена окна: " + (q2 ? "да" : "нет"));

        // Q3: from/to двери.
        var q3 = ContainsText(answerText, Case15OfficeName) &&
                 ContainsText(answerText, Case15CorridorName);
        notes.Add("Q3 from/to двери: " + (q3 ? "да" : "нет"));

        // Q4: состав «Офис 1» по семействам с количеством.
        var mentioned = 0;
        var countsOk = 0;
        var lines = answerText.Replace("\r", string.Empty).Split('\n');
        foreach (var pair in _case15OfficeContents)
        {
            var alias = Case15FamilyAlias(pair.Key);
            if (!ContainsText(answerText, alias))
                continue;

            mentioned++;
            var expected = pair.Value.ToString(CultureInfo.InvariantCulture);
            foreach (var line in lines)
            {
                if (ContainsText(line, alias) &&
                    line.IndexOf(expected, StringComparison.Ordinal) >= 0)
                {
                    countsOk++;
                    break;
                }
            }
        }

        var q4 = mentioned == _case15OfficeContents.Count &&
                 countsOk >= Math.Min(2, _case15OfficeContents.Count);
        notes.Add("Q4 состав «Офис 1»: " + (q4
            ? "да"
            : "семейств " + mentioned + "/" + _case15OfficeContents.Count + ", количества " + countsOk));

        // Q5: уровень насоса.
        var q5 = ContainsText(answerText, "этаж 1") && ContainsText(answerText, "Н1");
        notes.Add("Q5 уровень насоса: " + (q5 ? "да" : "нет"));

        // Q6: элементы разрыва + объяснение двух систем.
        var breakInAnswer = Case15AnswerHasId(answerText, _case15.PipeB2Id) &&
                            Case15AnswerHasId(answerText, _case15.PipeCId);
        var breakInCalls = false;
        foreach (var call in calls)
        {
            if (call.ToolName != "trace_system_chain" || !call.Success)
                continue;

            foreach (var (from, to) in Case15BreaksOf(call.ResultJson))
            {
                if ((from == _case15.PipeB2Id && to == _case15.PipeCId) ||
                    (from == _case15.PipeCId && to == _case15.PipeB2Id))
                {
                    breakInCalls = true;
                    break;
                }
            }

            if (breakInCalls)
                break;
        }

        var systemsExplained = ContainsText(answerText, Case15SystemSplit) &&
                               (ContainsText(answerText, "две систем") ||
                                ContainsText(answerText, "разрыв") ||
                                ContainsText(answerText, "не переход") ||
                                ContainsText(answerText, "не переда"));
        var q6 = (breakInAnswer || breakInCalls) && systemsExplained;
        notes.Add("Q6 разрыв и две системы: " + (q6
            ? "да"
            : "разрыв " + (breakInAnswer ? "в ответе" : breakInCalls ? "в вызовах" : "не найден") +
              ", объяснение " + (systemsExplained ? "да" : "нет")));

        var answeredCount = new[] { q1, q2, q3, q4, q5, q6 }.Count(ok => ok);

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        if (calls.Count > 45)
            notes.Add("перебор вместо графа связей: " + calls.Count + " вызовов (грейдер скорости)");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; верных ответов " + answeredCount + "/6" +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && answeredCount == 6 && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        if (redFlags.Count == 0 && answeredCount >= 4)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    private static string Case15FamilyAlias(string familyName)
    {
        if (ContainsText(familyName, "Окно") || ContainsText(familyName, "Window"))
            return "Окно";
        if (ContainsText(familyName, "Двер") || ContainsText(familyName, "Door"))
            return "Двер";
        if (ContainsText(familyName, "Насос") || ContainsText(familyName, "Pump"))
            return "Насос";
        return familyName;
    }

    private static bool Case15AnswerHasId(string answerText, long id) =>
        answerText.IndexOf(id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Истина по элементу увидена через get_element_relations: вызов с targetId
    /// в elementIds, и все expectedIds есть в его результате (соседи/хост).
    /// </summary>
    private static bool Case15RelationsSaw(
        List<BenchCall> calls,
        long targetId,
        IReadOnlyList<long> expectedIds)
    {
        foreach (var call in calls)
        {
            if (call.ToolName != "get_element_relations" || !call.Success)
                continue;
            if (!Case15ArgsContainId(call.ArgumentsJson, targetId))
                continue;

            var all = true;
            foreach (var expected in expectedIds)
            {
                if (call.ResultJson.IndexOf(
                        "\"elementId\":" + expected.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal) < 0)
                {
                    all = false;
                    break;
                }
            }

            if (all)
                return true;
        }

        return false;
    }

    private static bool Case15ArgsContainId(string argumentsJson, long id)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (!document.RootElement.TryGetProperty("elementIds", out var array) ||
                array.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.GetInt64() == id)
                    return true;
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — цель не извлечь.
        }

        return false;
    }

    private static List<(long From, long To)> Case15BreaksOf(string resultJson)
    {
        var result = new List<(long, long)>();
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("breaks", out var breaks) ||
                breaks.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in breaks.EnumerateArray())
            {
                result.Add((item.GetProperty("fromElementId").GetInt64(),
                    item.GetProperty("toElementId").GetInt64()));
            }
        }
        catch (JsonException)
        {
            // Обрезанный или не-JSON результат — пары разрывов не извлекаются.
        }

        return result;
    }
}
