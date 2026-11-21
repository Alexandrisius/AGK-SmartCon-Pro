using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Tool execution (write tools approved, job tools with a shared runner),
/// the migration verdict (batch-first + model state) and the state read-back
/// dump for the mass family migration dogfood (partial of
/// <see cref="AgentMigrationDogfoodTests"/>).
/// </summary>
public sealed partial class AgentMigrationDogfoodTests
{
    private string ExecuteTool(
        DogfoodToolCall call,
        IReadOnlyDictionary<string, IAgentTool> tools)
    {
        if (!tools.TryGetValue(call.Name, out var tool))
        {
            return "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Unknown tool " +
                   call.Name + "\"}";
        }

        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return "{\"success\":false,\"errorCode\":\"invalid_arguments\",\"message\":\"" +
                   ex.Message.Replace("\"", "'") + "\"}";
        }

        var result = tool switch
        {
            AgentWriteToolBase write => write.ExecuteForTests(args, approved: true, CancellationToken.None),
            AgentReadToolBase read => read.ExecuteForTests(Doc, args, CancellationToken.None),
            IAgentTool plain => ExecutePlainTool(plain, args),
            _ => null
        };

        return result is null
            ? "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Tool cannot be executed\"}"
            : ToolResultBudget.Serialize(result, 8000);
    }

    private ToolResult ExecutePlainTool(IAgentTool tool, JsonElement args) =>
        tool.ExecuteAsync(
                new AgentToolContext(new InlineAwaitableEvent(), DocumentId, Guid.Empty)
                {
                    WriteApproved = true
                },
                args,
                CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Green (1.0): the model started the replace_family_parameter job (not
    /// per-family tools), at least one preview preceded the apply, and all three
    /// families ended with the shared target parameter (new GUID, values kept)
    /// while the old one is gone. A correct model state reached by a wrong path
    /// (e.g. per-family writes) is a partial 0.5.
    /// </summary>
    private MigrationVerdict EvaluateMigration(
        IReadOnlyList<DogfoodCall> calls,
        IReadOnlyList<int> callsPerRound)
    {
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));
        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count;

        var replaceJobs = calls.Where(IsReplaceFamilyJobCall).ToList();
        var usedBatchJob = replaceJobs.Count > 0;
        var firstApply = replaceJobs.FindIndex(IsApplyJobCall);
        var previewBeforeApply = firstApply >= 0 &&
                                 replaceJobs.Take(firstApply)
                                     .Any(call => !IsApplyJobCall(call));

        var problems = CheckMigrationState();
        var notes = new List<string>();
        if (!usedBatchJob)
            notes.Add("массовая джоба replace_family_parameter не использована");
        else if (!previewBeforeApply)
            notes.Add("превью перед apply отсутствует");
        notes.AddRange(problems);

        if (notes.Count == 0)
        {
            return new MigrationVerdict(
                1.0,
                "PASS",
                detail + "; «" + NewParameterName + "» shared (GUID " +
                NewDefinitionGuid.ToString("D") + ") во всех " + _seeds.Count +
                " семействах, значения по типам сохранены, «" + OldParameterName + "» удалён");
        }

        return new MigrationVerdict(
            problems.Count == 0 ? 0.5 : 0.0,
            problems.Count == 0 ? "PARTIAL" : "FAIL",
            detail + "; " + string.Join("; ", notes));
    }

    private List<string> CheckMigrationState()
    {
        var problems = new List<string>();
        foreach (var seed in _seeds)
        {
            var marks = _marks[seed.FamilyName];
            var perType = new[]
            {
                (FamilySeed.Type1, marks.Type1),
                (FamilySeed.Type2, marks.Type2)
            };

            foreach (var (typeName, expected) in perType)
            {
                var parameter = seed.GetProjectParameter(typeName, NewParameterName);
                if (parameter is null)
                {
                    problems.Add("«" + seed.FamilyName + "»/" + typeName + ": «" +
                                 NewParameterName + "» отсутствует");
                    continue;
                }

                if (!parameter.IsShared)
                {
                    problems.Add("«" + seed.FamilyName + "»/" + typeName + ": «" +
                                 NewParameterName + "» не shared");
                }
                else if (parameter.GUID != NewDefinitionGuid)
                {
                    problems.Add("«" + seed.FamilyName + "»/" + typeName + ": GUID " +
                                 parameter.GUID.ToString("D") + " ≠ нового определения");
                }

                var actual = parameter.AsString();
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    problems.Add("«" + seed.FamilyName + "»/" + typeName + ": значение «" +
                                 (actual ?? "(null)") + "» ≠ «" + expected + "»");
                }
            }

            if (seed.GetProjectParameter(FamilySeed.Type1, OldParameterName) is not null ||
                seed.GetProjectParameter(FamilySeed.Type2, OldParameterName) is not null)
            {
                problems.Add("«" + seed.FamilyName + "»: «" + OldParameterName +
                             "» остался как параметр семейства");
            }
        }

        return problems;
    }

    private string DumpState()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Семейства «" + FamilyPrefix + "*»:");
        foreach (var seed in _seeds)
        {
            var marks = _marks[seed.FamilyName];
            builder.AppendLine("- «" + seed.FamilyName + "» (id " + seed.FamilyId + "):");
            builder.AppendLine("    " + OldParameterName + ": " +
                               DescribeFamilyParameter(seed, OldParameterName));
            builder.AppendLine("    " + NewParameterName + ": " +
                               DescribeFamilyParameter(seed, NewParameterName) +
                               " (ожидается Тип1=«" + marks.Type1 + "», Тип2=«" + marks.Type2 +
                               "», shared GUID " + NewDefinitionGuid.ToString("D") + ")");
        }

        return builder.ToString();
    }

    private string DescribeFamilyParameter(FamilySeed seed, string parameterName)
    {
        var type1 = seed.GetProjectParameter(FamilySeed.Type1, parameterName);
        if (type1 is null)
            return "(отсутствует)";

        var type2 = seed.GetProjectParameter(FamilySeed.Type2, parameterName);
        var binding = type1.IsShared
            ? "shared GUID " + type1.GUID.ToString("D")
            : "family";
        return binding + ", Тип1=«" + (type1.AsString() ?? "(null)") + "», Тип2=«" +
               (type2?.AsString() ?? "(null)") + "»";
    }

    private static bool IsReplaceFamilyJobCall(DogfoodCall call)
    {
        if (!string.Equals(call.ToolName, "start_job", StringComparison.Ordinal))
            return false;

        try
        {
            using var args = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return args.RootElement.TryGetProperty("action", out var action) &&
                   string.Equals(action.GetString(), "replace_family_parameter",
                       StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsApplyJobCall(DogfoodCall call)
    {
        try
        {
            using var args = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (args.RootElement.TryGetProperty("mode", out var mode) &&
                mode.ValueKind == JsonValueKind.String)
            {
                var text = mode.GetString();
                if (string.Equals(text, "apply", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "resume", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return args.RootElement.TryGetProperty("dryRun", out var dryRun) &&
                   dryRun.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record DogfoodCall(
        string ToolName,
        string ArgumentsJson,
        string ResultJson,
        bool Success,
        string? ErrorCode,
        int Round)
    {
        public static DogfoodCall From(DogfoodToolCall call, string resultJson, int round)
        {
            var success = false;
            string? errorCode = null;
            try
            {
                using var document = JsonDocument.Parse(resultJson);
                var root = document.RootElement;
                if (root.TryGetProperty("success", out var successValue) &&
                    successValue.ValueKind == JsonValueKind.True)
                {
                    success = true;
                }

                if (root.TryGetProperty("errorCode", out var codeValue) &&
                    codeValue.ValueKind == JsonValueKind.String)
                {
                    errorCode = codeValue.GetString();
                }
            }
            catch (JsonException)
            {
                // A malformed result is reported as a failure without a code.
            }

            return new DogfoodCall(
                call.Name, call.ArgumentsJson, resultJson, success, errorCode, round);
        }
    }

    private sealed record MigrationVerdict(double Score, string Label, string Detail);

    private sealed class InlineAwaitableEvent : IAgentAwaitableEvent
    {
        private static readonly object App = new();

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default) =>
            Task.FromResult(funcWithApp(App));

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            actionWithApp(App);
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }
}
