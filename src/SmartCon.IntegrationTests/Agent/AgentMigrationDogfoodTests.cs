using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Live dogfooding of the mass family migration (#430–#433): a real LLM must
/// replace the shared parameter ADSK_Марка with ТДСК_Марка across three seeded
/// families as one batch job (preview → apply), not per-family calls. The
/// shared parameter file is swapped to a temp one with both definitions seeded
/// in advance. The transcript lands in %TEMP%\smartcon-migration-dogfood plus a
/// stable copy on success (Nice3point substitutes TEMP with a GUID directory,
/// #445). Skipped unless SMARTCON_AGENT_LIVE=1 and an API key are configured.
/// </summary>
public sealed partial class AgentMigrationDogfoodTests : RevitApiTest
{
    private const int MaxRoundsPerTask = 6;
    private const int FamilyCount = 3;
    private const string FamilyPrefix = "SC_Мигр_";
    private const string OldParameterName = "ADSK_Марка";
    private const string NewParameterName = "ТДСК_Марка";
    private const string SharedGroupName = "Миграция";

    private static readonly Guid OldDefinitionGuid = new("6d5b9c2e-1a34-4e7f-9c58-b0d1e2f3a4b5");
    private static readonly Guid NewDefinitionGuid = new("3f8a1d6b-92c4-4b5e-8d73-a6c9e0f1b2d3");

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private AgentDatabase? _database;
    private string? _databasePath;
    private SqliteAgentJobStore? _store;
    private JobRunner? _runner;
    private IJobAction[]? _jobActions;
    private SharedFileScope? _sharedFile;
    private readonly List<FamilySeed> _seeds = new();
    private readonly Dictionary<string, (string Type1, string Type2)> _marks =
        new(StringComparer.OrdinalIgnoreCase);

    private Document Doc => _document!;

    private string DocumentId => AgentDocumentIdentity.GetDocumentId(Doc);

    private SqliteAgentJobStore Store => _store!;

    private JobRunner Runner => _runner!;

    [Test]
    public async Task LiveDogfood_MassFamilyParameterMigration()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedModel();
        var tools = CreateTools();
        var definitions = tools.Select(tool => tool.Definition).ToList();
        var byName = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);

        var transcript = new StringBuilder();
        transcript.AppendLine("# SmartCon Agent — mass family migration dogfood (#430–#433)");
        transcript.AppendLine();
        transcript.AppendLine("- Provider: " + config.Provider);
        transcript.AppendLine("- Model: " + config.Model);
        transcript.AppendLine("- Revit: " + Doc.Application.VersionNumber);
        transcript.AppendLine("- Started: " + DateTime.Now.ToString("O"));
        transcript.AppendLine("- Seeded model:");
        foreach (var line in SeedSummary())
            transcript.AppendLine("  - " + line);
        transcript.AppendLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);

        var tasks = BuildTasks();
        var requests = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        string? feedbackAnswer = null;
        MigrationVerdict? verdict = null;

        for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
        {
            var task = tasks[taskIndex];
            transcript.AppendLine("## " + (taskIndex + 1) + ". " + task);
            transcript.AppendLine();

            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "system", ["content"] = SystemPrompt() },
                new() { ["role"] = "user", ["content"] = task }
            };

            var calls = new List<DogfoodCall>();
            var callsPerRound = new List<int>();
            string? answer = null;

            for (var round = 1; round <= MaxRoundsPerTask; round++)
            {
                requests++;
                var response = DogfoodClient.CompleteAsync(
                        http, config, messages, definitions, CancellationToken.None)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                promptTokens += response.PromptTokens;
                completionTokens += response.CompletionTokens;

                if (response.Error is not null)
                {
                    transcript.AppendLine("**LLM error:** " + response.Error);
                    transcript.AppendLine();
                    break;
                }

                if (response.ToolCalls.Count == 0)
                {
                    answer = response.Content;
                    break;
                }

                callsPerRound.Add(response.ToolCalls.Count);

                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = response.Content,
                    ["tool_calls"] = response.ToolCalls.Select(call => new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson
                        }
                    }).ToList()
                });

                foreach (var call in response.ToolCalls)
                {
                    var resultJson = ExecuteTool(call, byName);
                    var record = DogfoodCall.From(call, resultJson, round);
                    calls.Add(record);

                    transcript.AppendLine(
                        "- r" + round + " `" + call.Name + "` " +
                        Shorten(call.ArgumentsJson, 240));
                    transcript.AppendLine(
                        "  → " + (record.Success ? "ok" : "FAIL " + record.ErrorCode) + " " +
                        Shorten(resultJson, 420));

                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = resultJson
                    });
                }
            }

            transcript.AppendLine();
            transcript.AppendLine("**Answer:** " + (answer ?? "(no final answer)"));

            if (taskIndex == 0)
            {
                verdict = EvaluateMigration(calls, callsPerRound);
                transcript.AppendLine("**Verdict:** " + verdict.Label + " — " + verdict.Detail);
            }
            else
            {
                feedbackAnswer = answer;
                transcript.AppendLine(
                    string.IsNullOrWhiteSpace(answer)
                        ? "**Verdict:** FAIL — TASK FAILED (нет ответа на вопрос обратной связи)"
                        : "**Verdict:** FEEDBACK CAPTURED");
            }

            transcript.AppendLine();
            transcript.AppendLine("### State read-back");
            transcript.AppendLine(DumpState());
            transcript.AppendLine();
        }

        transcript.AppendLine("## Harness feedback");
        transcript.AppendLine();
        transcript.AppendLine(feedbackAnswer ?? "(модель не ответила на вопрос обратной связи)");
        transcript.AppendLine();

        transcript.AppendLine("## Summary");
        transcript.AppendLine();
        transcript.AppendLine("- Migration verdict: " + (verdict is null ? "?" : verdict.Label) +
                              " (score " + (verdict is null ? "?" : verdict.Score.ToString("0.0",
                                  System.Globalization.CultureInfo.InvariantCulture)) + ")");
        transcript.AppendLine("- Feedback answer: " +
                              (string.IsNullOrWhiteSpace(feedbackAnswer) ? "no" : "yes"));
        transcript.AppendLine("- LLM requests: " + requests);
        transcript.AppendLine("- Tokens: prompt " + promptTokens + ", completion " +
                               completionTokens);
        transcript.AppendLine("- Finished: " + DateTime.Now.ToString("O"));

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-migration-dogfood");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "transcript.md");
        File.WriteAllText(path, transcript.ToString(), Encoding.UTF8);
        Console.WriteLine("Migration dogfood transcript: " + path);

        if (verdict is not null && verdict.Score >= 1.0)
            CopyTranscriptToStablePath(path);

        await Assert.That(verdict!.Score).IsGreaterThanOrEqualTo(1.0);
        await Assert.That(string.IsNullOrWhiteSpace(feedbackAnswer)).IsFalse();
    }

    [After(Test)]
    public void Close()
    {
        foreach (var seed in _seeds)
            seed.Dispose();

        _seeds.Clear();
        _sharedFile?.Dispose();
        _document?.Close(false);

        if (_databasePath is not null && File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    private void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "sc-migrdogfood-" + Guid.NewGuid().ToString("N") + ".db");
        _database = new AgentDatabase(_databasePath);
        _database.Initialize();
        _store = new SqliteAgentJobStore(_database);
        _jobActions = new IJobAction[]
        {
            new MapFamilyParametersJobAction(),
            new ReplaceFamilyParameterJobAction(),
            new RenameViewsJobAction(),
            new SetParametersJobAction()
        };
        _runner = new JobRunner(_store, _jobActions, _transactions);
        // The test host has no ExternalEvent pump: get_job_status drives the job
        // forward on the same thread, mirroring the production pump handler.
        _runner.Initialize(() => _runner.RunToCompletion(Doc, TimeSpan.FromMinutes(3)));

        for (var index = 1; index <= FamilyCount; index++)
        {
            var seed = FamilySeed.Create(Application, Doc, FamilyPrefix + index, out var reason);
            if (seed is null)
            {
                Skip.Test(reason ?? "Не удалось сидировать семейство");
                return;
            }

            _seeds.Add(seed);
            _marks[seed.FamilyName] = ("М1-" + index, "М2-" + index);
        }

        _sharedFile = SharedFileScope.Create(Application);
        var definitions = SeedSharedDefinitions();
        for (var index = 0; index < _seeds.Count; index++)
        {
            var marks = _marks[_seeds[index].FamilyName];
            SeedOldMarkParameter(
                definitions.Old, _seeds[index].FamilyName, marks.Type1, marks.Type2);
        }

        VerifySeed();
    }

    private (ExternalDefinition Old, ExternalDefinition New) SeedSharedDefinitions()
    {
        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.Create(SharedGroupName);
        var oldDefinition = (ExternalDefinition)group.Definitions.Create(
            new ExternalDefinitionCreationOptions(OldParameterName, SpecTypeId.String.Text)
            {
                GUID = OldDefinitionGuid
            });
        var newDefinition = (ExternalDefinition)group.Definitions.Create(
            new ExternalDefinitionCreationOptions(NewParameterName, SpecTypeId.String.Text)
            {
                GUID = NewDefinitionGuid
            });
        return (oldDefinition, newDefinition);
    }

    private void SeedOldMarkParameter(
        ExternalDefinition definition,
        string familyName,
        string type1Value,
        string type2Value)
    {
        var family = FindFamilyByName(familyName);
        if (family is null)
        {
            Skip.Test("Сид-семейство «" + familyName + "» не найдено после загрузки");
            return;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(family);
            var manager = familyDoc.FamilyManager;
            using (var transaction = new Transaction(familyDoc, "Seed " + OldParameterName))
            {
                transaction.Start();
                var parameter = manager.AddParameter(definition, GroupTypeId.General, false);
                foreach (FamilyType type in manager.Types)
                {
                    string? value;
                    if (string.Equals(type.Name, FamilySeed.Type1, StringComparison.OrdinalIgnoreCase))
                        value = type1Value;
                    else if (string.Equals(type.Name, FamilySeed.Type2, StringComparison.OrdinalIgnoreCase))
                        value = type2Value;
                    else
                        continue;

                    manager.CurrentType = type;
                    manager.Set(parameter, value);
                }

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    Skip.Test("Не удалось записать «" + OldParameterName + "» в «" + familyName + "»");
                    return;
                }
            }

            familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
                familyDoc.Close(false);
        }
    }

    private void VerifySeed()
    {
        foreach (var seed in _seeds)
        {
            var marks = _marks[seed.FamilyName];
            var old1 = seed.GetProjectParameter(FamilySeed.Type1, OldParameterName);
            var old2 = seed.GetProjectParameter(FamilySeed.Type2, OldParameterName);
            if (old1 is null || old2 is null ||
                !string.Equals(old1.AsString(), marks.Type1, StringComparison.Ordinal) ||
                !string.Equals(old2.AsString(), marks.Type2, StringComparison.Ordinal))
            {
                Skip.Test("Сид «" + seed.FamilyName + "» не получил «" + OldParameterName +
                          "» со значениями по типам");
                return;
            }

            if (seed.GetProjectParameter(FamilySeed.Type1, NewParameterName) is not null)
            {
                Skip.Test("Сид «" + seed.FamilyName + "» уже содержит «" + NewParameterName +
                          "» — сид некорректен");
                return;
            }
        }
    }

    private RevitFamily? FindFamilyByName(string familyName)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily family &&
                string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return null;
    }

    private List<string> SeedSummary()
    {
        var lines = new List<string>();
        foreach (var seed in _seeds)
        {
            var marks = _marks[seed.FamilyName];
            lines.Add("family «" + seed.FamilyName + "» (id " + seed.FamilyId + "): shared «" +
                      OldParameterName + "» (GUID " + OldDefinitionGuid.ToString("D") +
                      "), Тип1=«" + marks.Type1 + "», Тип2=«" + marks.Type2 + "»");
        }

        lines.Add("ФОП подменён на временный файл, группа «" + SharedGroupName + "»: «" +
                  OldParameterName + "» (GUID " + OldDefinitionGuid.ToString("D") + ") и «" +
                  NewParameterName + "» (GUID " + NewDefinitionGuid.ToString("D") + ")");
        return lines;
    }

    private List<IAgentTool> CreateTools()
    {
        var context = new StubRevitContext(Doc);
        return new List<IAgentTool>
        {
            new ListFamiliesTool(context),
            new ListFamilyTypesTool(context),
            new GetFamilyParametersTool(context),
            new FindFamiliesWithParameterTool(context),
            new ListSharedParametersTool(context),
            new StartJobTool(Runner, Store, context),
            new GetJobStatusTool(Store, Runner),
            new GetJobReportTool(Store, new JobReportBuilder(Store, _jobActions!), context),
            new RollbackJobTool(Store, Runner, context),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };
    }

    private static List<string> BuildTasks() => new()
    {
        "Мы переезжаем на новый стандарт общих параметров. Во всех загруженных семействах серии " +
        FamilyPrefix + " замени общий параметр " + OldParameterName + " на " + NewParameterName +
        " — оба определения уже есть в подключённом файле общих параметров. " +
        "Значения по типам должны сохраниться. Действуй массовой операцией, не по одному " +
        "семейству за раз.",

        "Оцени по 5-балльной шкале, насколько удобно выполнить такую массовую миграцию: " +
        "что удобно, что неудобно, чего не хватило?"
    };

    private static string SystemPrompt() =>
        "Ты SmartCon Agent внутри Autodesk Revit и работаешь с семействами и общими " +
        "параметрами через инструменты. Отвечай по-русски. Помни: family ≠ типоразмер " +
        "(FamilySymbol) ≠ экземпляр (FamilyInstance). Сначала читай проект (list_families, " +
        "get_family_parameters, list_shared_parameters), не выдумывай имена, id и GUID — " +
        "бери их из результатов. Массовые операции по многим семействам выполняй пакетной " +
        "джобой с обязательным превью до apply (dryRun=true → previewDigest → dryRun=false) " +
        "и дожидайся терминального состояния по статусу джобы. Поэлементные операции записи " +
        "по умолчанию идут с dryRun=true; применяй (dryRun=false), когда пользователь прямо " +
        "просит выполнить. Если инструмент вернул ошибку, объясни её пользователю и не " +
        "повторяй тот же вызов без изменений. Отвечай кратко: что сделано и ключевые числа.";

    private static void CopyTranscriptToStablePath(string source)
    {
        try
        {
            // Nice3point substitutes TEMP with a GUID directory (#445): keep a copy
            // at a stable path the operator can open.
            var stableDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "smartcon-migration-dogfood-stable");
            Directory.CreateDirectory(stableDirectory);
            var destination = Path.Combine(stableDirectory, "transcript.md");
            File.Copy(source, destination, overwrite: true);
            Console.WriteLine("Migration dogfood stable transcript: " + destination);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Stable transcript copy failed: " + ex.GetType().Name + ": " +
                              ex.Message);
        }
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    /// <summary>
    /// Swaps the shared parameter file to a temp file so the dogfood run never
    /// touches the user's ФОП.
    /// </summary>
    private sealed class SharedFileScope : IDisposable
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _application;
        private readonly string? _previous;
        private readonly string? _temporary;

        private SharedFileScope(
            Autodesk.Revit.ApplicationServices.Application application,
            string temporary)
        {
            _application = application;
            _previous = application.SharedParametersFilename;
            _temporary = temporary;
        }

        public static SharedFileScope Create(
            Autodesk.Revit.ApplicationServices.Application application)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SmartConMigrationDogfood");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "shared-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, string.Empty);
            var scope = new SharedFileScope(application, path);
            application.SharedParametersFilename = path;
            return scope;
        }

        public void Dispose()
        {
            try
            {
                _application.SharedParametersFilename = _previous ?? string.Empty;
            }
            catch (Exception)
            {
                // Restoring the shared parameter file is best-effort in the test host.
            }

            try
            {
                if (_temporary is not null && File.Exists(_temporary))
                    File.Delete(_temporary);
            }
            catch (Exception)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }
}
