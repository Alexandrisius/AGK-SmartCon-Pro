using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Budget mechanics of the production-loop harness (C7-03/C2-02, C2-12, C2-13):
/// the steering thresholds (~80 % of the round budget, ~75 % of the time
/// budget) with the verbatim steering/final-demand texts, the final call
/// without tools after the budget is spent, the artifact field limiter (full
/// values up to 20 000 chars; longer values become a VALID JSON wrapper and
/// the full text lands in a file next to the artifact — «Unterminated string»
/// при разборе больше невозможен) and the token-usage reader from the session
/// database (the sum of EVERY completed LLM call, including the turns that
/// were cancelled by the budget or by steering).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    /// <summary>Верхний предел одного строкового поля артефакта (args/message).</summary>
    internal const int ArtifactFieldLimitChars = 20000;

    /// <summary>Запас времени на финальный вызов после исчерпания бюджета.</summary>
    private const int BudgetFinalCallSeconds = 180;

    /// <summary>Порог steering по раундам: ~80% бюджета, не меньше 1.</summary>
    internal static int SteeringRoundThreshold(int maxRounds) =>
        Math.Max(1, (int)Math.Ceiling(maxRounds * 0.8));

    /// <summary>Порог steering по времени: ~75% бюджета, не меньше 30 с.</summary>
    internal static int SteeringTimeThresholdSeconds(int maxSeconds) =>
        Math.Max(30, (int)(maxSeconds * 0.75));

    internal const string SteeringTimeText =
        "[Харнесс бенчмарка] Время на задачу заканчивается: прошло около 75% бюджета. " +
        "Инструменты ещё доступны. Не начинай длинных новых действий и пропусти " +
        "необязательные проверки. Если по заданию остался невыполненный результат, для " +
        "которого нужно один-два вызова, — сначала заверши его, затем сразу дай " +
        "финальный отчёт по пунктам задачи (что сделано, что не сделано и почему), " +
        "включая блок обратной связи Harness feedback (что было удобно; что неудобно; " +
        "что оказалось невозможно; чего не хватило).";

    internal const string SteeringRoundsText =
        "[Харнесс бенчмарка] Лимит раундов инструментальных вызовов почти исчерпан: " +
        "пройдено около 80% бюджета. Инструменты ещё доступны. Не начинай длинных " +
        "новых действий и пропусти необязательные проверки. Если по заданию остался " +
        "невыполненный результат, для которого нужно один-два вызова, — сначала " +
        "заверши его, затем сразу дай финальный отчёт по пунктам задачи (что сделано, " +
        "что не сделано и почему), включая блок обратной связи Harness feedback (что " +
        "было удобно; что неудобно; что оказалось невозможно; чего не хватило).";

    internal const string FinalAnswerDemandText =
        "[Харнесс бенчмарка] Бюджет инструментальных вызовов исчерпан — инструментов " +
        "больше нет, этот вызов не может исполнять тулы. Дай финальный ответ СЕЙЧАС: " +
        "итоговый отчёт по пунктам задачи (что реально сделано, что не сделано и почему) " +
        "и блок обратной связи Harness feedback (что было удобно; что неудобно; что " +
        "оказалось невозможно; чего не хватило). Не описывай результаты, которых нет.";

    /// <summary>
    /// Полное строковое поле артефакта, содержимое парсится как JSON (args):
    /// до лимита — как есть; свыше — ВАЛИДНАЯ JSON-обёртка с префиксом и общей
    /// длиной, а полный текст сохраняется файлом <paramref name="fullTextPath"/>
    /// рядом с артефактом (обрыв посреди JSON-строки давал «Unterminated string»).
    /// </summary>
    internal static string ArtifactJsonField(
        string value,
        string fullTextPath,
        List<(string RelativePath, string Text)> oversized)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= ArtifactFieldLimitChars)
            return value ?? string.Empty;

        oversized.Add((fullTextPath, value));
        return "{\"truncated\":true,\"totalLength\":" +
               value.Length.ToString(CultureInfo.InvariantCulture) +
               ",\"fullTextFile\":" + JsonSerializer.Serialize(fullTextPath) +
               ",\"prefix\":" + JsonSerializer.Serialize(value[..ArtifactFieldLimitChars]) + "}";
    }

    /// <summary>
    /// Текстовое поле артефакта (message): до лимита — полностью; свыше —
    /// префикс лимита с явным маркером обрезки и путём к полному тексту.
    /// </summary>
    internal static string ArtifactTextField(
        string value,
        string fullTextPath,
        List<(string RelativePath, string Text)> oversized)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= ArtifactFieldLimitChars)
            return value ?? string.Empty;

        oversized.Add((fullTextPath, value));
        return value[..ArtifactFieldLimitChars] + " …[обрезано харнессом, всего " +
               value.Length.ToString(CultureInfo.InvariantCulture) + " симв.; полный текст: " +
               fullTextPath + "]";
    }

    /// <summary>Относительный путь файла полного текста поля (рядом с артефактом).</summary>
    internal static string OversizedFieldPath(string artifactStamp, int callNumber, string fileStem) =>
        artifactStamp + "-calls/" + callNumber.ToString("000", CultureInfo.InvariantCulture) +
        "-" + fileStem;

    /// <summary>Пишет полные тексты oversized-полей в каталог &lt;stamp&gt;-calls рядом с артефактом.</summary>
    internal static void WriteOversizedCallFields(
        string jsonPath,
        List<(string RelativePath, string Text)> oversized)
    {
        if (oversized.Count == 0)
            return;

        var root = Path.GetDirectoryName(jsonPath);
        if (string.IsNullOrEmpty(root))
            return;

        foreach (var (relativePath, text) in oversized)
        {
            try
            {
                var target = Path.Combine(root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Benchmark oversized call field not written: " + relativePath +
                                  " — " + ex.GetType().Name);
            }
        }

        Console.WriteLine("Benchmark oversized call fields written: " + oversized.Count +
                          " → " + Path.Combine(root, Path.GetFileName(Path.GetDirectoryName(
                              oversized[0].RelativePath)) ?? string.Empty));
    }

    /// <summary>
    /// Финальный вызов после исчерпания бюджета (C2-02): тулсет сессии
    /// опустошается (frozen остаётся замороженным, но пустым — оркестратор не
    /// пере-выберет тулы), поэтому LLM-запрос уходит без инструментов и модель
    /// может только дать финальный текст. Отдельный токен с запасом
    /// <see cref="BudgetFinalCallSeconds"/> с: бюджет уже исчерпан, но финальный
    /// ответ не должен теряться. Без счётчика раундов: это не рабочий раунд.
    /// </summary>
    private static string? RunBudgetFinalAnswerTurn(
        ProductionAgentHost host,
        Guid conversationId,
        List<AgentTurnResult> turnResults,
        List<string> notes)
    {
        host.Session.FrozenToolNames.Clear();
        host.Session.LoadedToolNames.Clear();
        host.Session.PendingToolNames.Clear();
        host.Session.ToolSetFrozen = true;

        using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(BudgetFinalCallSeconds));

        // «Без инструментов» гарантирует и хост, а не только пустой тулсет
        // запроса: провайдер не возвращает tool_calls без tools, но если
        // модель всё же прислала вызов, он исполняется один раз, после чего
        // финальный turn останавливается — финальный вызов не становится
        // способом продолжить работу за пределами бюджета.
        var strayToolCalls = 0;
        var guard = new InlineProgress(progress =>
        {
            if (progress.Kind != AgentProgressKind.ToolCompleted)
                return;

            Interlocked.Increment(ref strayToolCalls);
            try
            {
                finalCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Финальный turn уже завершился.
            }
        });

        try
        {
            var finalTurn = PumpOnRevitThread(host.Orchestrator.RunTurnAsync(
                conversationId,
                FinalAnswerDemandText,
                Array.Empty<AgentAttachment>(),
                guard,
                finalCts.Token));
            turnResults.Add(finalTurn);
            if (finalTurn.ToolCallCount > 0)
                notes.Add("Финальный вызов без инструментов: модель прислала " +
                          finalTurn.ToolCallCount + " тул-вызов(а) — исполнение остановлено.");
            if (string.IsNullOrWhiteSpace(finalTurn.FinalText))
                notes.Add("Финальный вызов без инструментов вернул пустой текст.");
            else
                notes.Add("Финальный вызов без инструментов: ответ " + finalTurn.FinalText.Length +
                          " симв., раундов " + finalTurn.Rounds + ", тул-вызовов " +
                          finalTurn.ToolCallCount + ".");
            return finalTurn.FinalText;
        }
        catch (OperationCanceledException) when (finalCts.IsCancellationRequested)
        {
            notes.Add("Финальный вызов остановлен: " +
                      (strayToolCalls > 0
                          ? "модель пыталась вызывать инструменты без тулсета (" +
                            strayToolCalls + ")"
                          : "превышён запас " + BudgetFinalCallSeconds + " с"));
            return null;
        }
        catch (Exception ex)
        {
            notes.Add("Финальный вызов не удался: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Суммарный usage всех завершённых LLM-вызовов из таблицы token_usage
    /// сессионной базы хоста (оркестратор пишет запись и у отменённого по
    /// бюджету/steering'у turn'а — статус «cancelled», поэтому сумма не
    /// зануляется). found=false — записей нет (fallback на сумму по вернувшимся
    /// turn'ам).
    /// </summary>
    internal static (bool Found, int PromptTokens, int CompletionTokens, int ReasoningTokens, int CachedTokens)
        ReadTokenUsage(string databasePath, string sessionId)
    {
        try
        {
            if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath))
                return (false, 0, 0, 0, 0);

            using var connection = new SqliteConnection(
                "Data Source=" + databasePath + ";Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*), COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(completion_tokens),0), " +
                "COALESCE(SUM(reasoning_tokens),0), COALESCE(SUM(cached_tokens),0) " +
                "FROM token_usage WHERE session_id = @sessionId";
            command.Parameters.AddWithValue("@sessionId", sessionId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) == 0)
                return (false, 0, 0, 0, 0);

            return (true, (int)reader.GetInt64(1), (int)reader.GetInt64(2),
                (int)reader.GetInt64(3), (int)reader.GetInt64(4));
        }
        catch (Exception ex)
        {
            Console.WriteLine("Bench token_usage read failed: " + ex.GetType().Name + ": " + ex.Message);
            return (false, 0, 0, 0, 0);
        }
    }
}
