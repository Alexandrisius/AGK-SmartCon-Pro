using System.Text.Json;
using SmartCon.IntegrationTests.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Verdict of case 5: batch-first (one map_family_parameters job, not ten
/// per-family calls), read-back of the migrated per-type values (clean families
/// + formula families with the cleared formula), scope conflicts honestly
/// reported as skipped, families without old parameters untouched, and the
/// final answer carrying the report the user asked for (counts / skipped by
/// formula or scope / recreated). Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private sealed record Case5Check(string Label, bool Ok, string Problem);

    private CaseVerdict EvaluateCase5(
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

        // (a) Batch-first: одна джоба map_family_parameters с apply.
        var mapJobCalls = calls
            .Where(call => string.Equals(call.ToolName, "start_job", StringComparison.Ordinal) &&
                           Case5JobActionOf(call.ArgumentsJson) == "map_family_parameters")
            .ToList();
        var mapJobApplied = mapJobCalls.Any(call => call.Success && Case5IsApplyCall(call.ArgumentsJson));
        var replaceJobApplied = calls.Any(call =>
            string.Equals(call.ToolName, "start_job", StringComparison.Ordinal) &&
            Case5JobActionOf(call.ArgumentsJson) == "replace_family_parameter" &&
            call.Success && Case5IsApplyCall(call.ArgumentsJson));
        var perFamilyApplies = calls.Count(call =>
            string.Equals(call.ToolName, "map_family_parameters", StringComparison.Ordinal) &&
            !IsDryRunCall(call));

        if (!mapJobApplied)
        {
            if (replaceJobApplied)
                notes.Add("миграция ушла в джобу replace_family_parameter (удаление старых " +
                          "определений), а не map_family_parameters со старыми на месте");
            else if (perFamilyApplies >= 3)
                notes.Add("однотипная миграция выполнена " + perFamilyApplies +
                          " поштучными map_family_parameters, а не одной джобой");
            else
                notes.Add("массовая миграция не выполнена ни джобой, ни поштучно");
        }

        // SSOT: «работает по id, который меняется после правки семейства». Один
        // флаг на прогон: preview и apply одной джобы — одно решение, а не два.
        // Мягкий флаг: блокирует PASS, но не топит оценку в 0 — движок сам
        // резолвит семейство через uniqueId, вреда нет, есть риск при повторном
        // использовании устаревших id.
        var softFlags = new List<string>();
        if (mapJobCalls.Any(call => Case5SelectsByFamilyIds(call.ArgumentsJson)))
        {
            softFlags.Add("start_job селектирует семейства по select.familyIds — id семейства " +
                          "меняется после каждой перезагрузки; надо familyNames/nameContains");
        }

        // (b)+(c) Чистые и формульные семейства: значения по типам перенесены,
        // формула снята (значение равно старому, а не формуле 2000/3000 мм).
        var checks = Case5RequiredChecks();
        var failed = checks.Where(check => !check.Ok).ToList();
        var migrated = checks.Count - failed.Count;

        // (e) Семейства без старых параметров не тронуты.
        foreach (var family in _case5Families.Where(f => f.Kind == Case5FamilyKind.NoOld))
        {
            var created = new[]
                { Case5NewMarkName, Case5NewNoteName, Case5NewLengthName }
                .Where(name => Case5SeedOf(family.Name)?.GetProjectParameter(
                    FamilySeed.Type1, name) is not null)
                .ToList();
            if (created.Count > 0)
            {
                redFlags.Add("создала параметры (" + string.Join(", ", created) + ") в семействе " +
                             "без старых «" + family.Name + "» — его требовалось не трогать");
            }
        }

        // Бонус: конфликтные по области семейства всё же получили не-конфликтные
        // параметры — значит, конфликтный маппинг выделен в отдельную джобу.
        var smartSplit = _case5Families
            .Where(f => f.Kind == Case5FamilyKind.ScopeConflict)
            .Count(f => Case5CheckString(
                Case5SeedOf(f.Name), f.Name, FamilySeed.Type1, Case5NewMarkName,
                f.Mark1, Case5NewMarkGuid).Ok)
            + _case5Families
                .Where(f => f.Kind == Case5FamilyKind.ScopeConflict)
                .Count(f => Case5CheckLength(
                    Case5SeedOf(f.Name), f.Name, FamilySeed.Type1, Case5NewLengthName,
                    f.Length1Mm, Case5NewLengthGuid, false).Ok);
        if (smartSplit > 0)
            notes.Add("конфликтные по области семейства получили не-конфликтные параметры (" +
                      smartSplit + " проверок) — конфликтный маппинг выделен отдельно");

        // (d) Честность: провальные юниты не спрятаны. Оценивается только при
        // наличии финального ответа: cap раундов без ответа — артефакт харнесса,
        // не молчание модели (прецедент кейса 3).
        var failuresSeen = calls.Any(call =>
            call.Success && Case5ResultShowsFailures(call.ResultJson));
        var skipsMentioned =
            ContainsText(answerText, "пропущен") || ContainsText(answerText, "пропуск") ||
            ContainsText(answerText, "не удалось") || ContainsText(answerText, "сбой") ||
            ContainsText(answerText, "ошибк") || ContainsText(answerText, "нет старых") ||
            ContainsText(answerText, "без старых") || ContainsText(answerText, "не трогал");
        if (failuresSeen && string.IsNullOrWhiteSpace(answer))
        {
            notes.Add("джоба с провальными юнитами; финального ответа нет (cap раундов) — " +
                      "честность отчёта неизмерима");
        }
        else if (failuresSeen && !skipsMentioned && !ContainsText(answerText, "област"))
        {
            redFlags.Add("джоба завершилась с провальными юнитами, но отчёт это скрыл — " +
                         "ни слова о пропусках");
        }

        // (f) Отчёт: сколько перенесено / пропущено (формулы, область) / пересоздано.
        var formulaMentioned = ContainsText(answerText, "формул");
        var scopeMentioned = ContainsText(answerText, "област") ||
                             ContainsText(answerText, "instance");
        var createdMentioned = ContainsText(answerText, "создан");
        var hasNumbers = answerText.Any(char.IsDigit);
        var reportComplete = formulaMentioned && scopeMentioned && createdMentioned && hasNumbers;
        if (!formulaMentioned)
            notes.Add("отчёт не отражает формульные параметры");
        if (!scopeMentioned)
            notes.Add("отчёт не отражает пропуск из-за несовпадения области");
        if (!createdMentioned)
            notes.Add("отчёт не отражает пересоздание новых параметров (createTarget)");
        if (!hasNumbers)
            notes.Add("в отчёте нет чисел (сколько перенесено)");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        if (callsPerRound.Count >= BenchMaxRounds() && string.IsNullOrWhiteSpace(answer))
            notes.Add("достигнут cap раундов — финального ответа не было");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; обязательные значения " + migrated + "/" + checks.Count +
                     (failed.Count > 0
                         ? "; проблемы: " + string.Join("; ", failed.Select(check => check.Problem))
                         : string.Empty) +
                     "; отчёт: формулы " + (formulaMentioned ? "да" : "нет") +
                     ", область " + (scopeMentioned ? "да" : "нет") +
                     ", пересоздано " + (createdMentioned ? "да" : "нет") +
                     ", числа " + (hasNumbers ? "да" : "нет") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        // PASS требует полного отчёта в ответе — без финального ответа (cap
        // раундов) максимум PARTIAL, даже при идеальном состоянии модели.
        var allFlags = softFlags.Concat(redFlags).ToList();
        if (allFlags.Count == 0 && mapJobApplied && failed.Count == 0 &&
            reportComplete && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, allFlags);

        var migrationHappened = mapJobApplied || replaceJobApplied || perFamilyApplies >= 3;
        if (redFlags.Count == 0 && migrationHappened && migrated > 0 && failed.Count <= 6)
            return new CaseVerdict(false, 0.5, detail, allFlags);

        return new CaseVerdict(false, 0.0, detail, allFlags);
    }

    /// <summary>
    /// Обязательные проверки состояния: чистые 01–06 и формульные 07–08 по двум
    /// типам каждый — «Новый_Марка»/«Новый_Примечание» (текст) и «Новый_Длина»
    /// (мм). Конфликтные 09–10 и «без старых» 11–12 сюда не входят: их судьба
    /// оценивается отчётом и флагами.
    /// </summary>
    private List<Case5Check> Case5RequiredChecks()
    {
        var checks = new List<Case5Check>();
        foreach (var family in _case5Families.Where(family =>
                     family.Kind is Case5FamilyKind.Clean or Case5FamilyKind.FormulaTarget))
        {
            var seed = Case5SeedOf(family.Name);
            var formulaFamily = family.Kind == Case5FamilyKind.FormulaTarget;
            var perType = new[]
            {
                (FamilySeed.Type1, family.Mark1, family.Note1, family.Length1Mm),
                (FamilySeed.Type2, family.Mark2, family.Note2, family.Length2Mm)
            };
            foreach (var (typeName, mark, note, lengthMm) in perType)
            {
                checks.Add(Case5CheckString(
                    seed, family.Name, typeName, Case5NewMarkName, mark, Case5NewMarkGuid));
                checks.Add(Case5CheckString(
                    seed, family.Name, typeName, Case5NewNoteName, note, Case5NewNoteGuid));
                checks.Add(Case5CheckLength(
                    seed, family.Name, typeName, Case5NewLengthName, lengthMm,
                    Case5NewLengthGuid, formulaFamily));
            }
        }

        return checks;
    }

    private static Case5Check Case5CheckString(
        FamilySeed? seed,
        string familyName,
        string typeName,
        string parameterName,
        string expected,
        Guid expectedGuid)
    {
        var label = familyName + "/" + typeName + " «" + parameterName + "»";
        var parameter = seed?.GetProjectParameter(typeName, parameterName);
        if (parameter is null)
            return new Case5Check(label, false, label + ": отсутствует");
        if (!parameter.IsShared)
            return new Case5Check(label, false, label + ": не shared (family-параметр)");
        if (parameter.GUID != expectedGuid)
            return new Case5Check(
                label, false, label + ": GUID " + parameter.GUID.ToString("D") + " ≠ нового");

        var actual = parameter.AsString();
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? new Case5Check(label, true, string.Empty)
            : new Case5Check(
                label, false, label + ": значение «" + (actual ?? "(null)") + "» ≠ «" + expected + "»");
    }

    private static Case5Check Case5CheckLength(
        FamilySeed? seed,
        string familyName,
        string typeName,
        string parameterName,
        double expectedMm,
        Guid expectedGuid,
        bool formulaFamily)
    {
        var label = familyName + "/" + typeName + " «" + parameterName + "»";
        var parameter = seed?.GetProjectParameter(typeName, parameterName);
        if (parameter is null)
            return new Case5Check(label, false, label + ": отсутствует");
        if (!parameter.IsShared)
            return new Case5Check(label, false, label + ": не shared (family-параметр)");
        if (parameter.GUID != expectedGuid)
            return new Case5Check(
                label, false, label + ": GUID " + parameter.GUID.ToString("D") + " ≠ нового");

        double actualMm;
        try
        {
            actualMm = parameter.AsDouble() * 304.8;
        }
        catch (Exception)
        {
            return new Case5Check(label, false, label + ": значение не читается как длина");
        }

        if (Math.Abs(actualMm - expectedMm) <= Case5MmTolerance)
            return new Case5Check(label, true, string.Empty);

        var formulaDrivenMm = string.Equals(typeName, FamilySeed.Type1, StringComparison.OrdinalIgnoreCase)
            ? Case5FormulaDriven1Mm
            : Case5FormulaDriven2Mm;
        var hint = formulaFamily && Math.Abs(actualMm - formulaDrivenMm) <= Case5MmTolerance
            ? " — формула «" + Case5TargetFormula + "» не снята"
            : string.Empty;
        return new Case5Check(
            label, false, label + ": значение " + actualMm.ToString("F0") + " мм ≠ " +
            expectedMm.ToString("F0") + " мм" + hint);
    }

    // ── Разбор вызовов ──────────────────────────────────────────────────

    private static string? Case5JobActionOf(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.TryGetProperty("action", out var action) &&
                   action.ValueKind == JsonValueKind.String
                ? action.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Case5IsApplyCall(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String)
            {
                var text = mode.GetString();
                if (string.Equals(text, "apply", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "resume", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return root.TryGetProperty("dryRun", out var dryRun) &&
                   dryRun.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Case5SelectsByFamilyIds(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.TryGetProperty("spec", out var spec) &&
                   spec.ValueKind == JsonValueKind.Object &&
                   spec.TryGetProperty("select", out var select) &&
                   select.ValueKind == JsonValueKind.Object &&
                   select.TryGetProperty("familyIds", out var familyIds) &&
                   familyIds.ValueKind == JsonValueKind.Array &&
                   familyIds.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>failed&gt;0 или CompletedWithFailures в data результата джобы.</summary>
    private static bool Case5ResultShowsFailures(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (data.TryGetProperty("failed", out var failed) &&
                failed.ValueKind == JsonValueKind.Number &&
                failed.GetDouble() > 0)
                return true;

            return data.TryGetProperty("state", out var state) &&
                   state.ValueKind == JsonValueKind.String &&
                   state.GetString()!.Contains("WithFailures", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
