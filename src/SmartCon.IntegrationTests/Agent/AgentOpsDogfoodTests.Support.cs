using System.IO;
using System.Text.Json;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Shared plumbing of the G.2–G.4 ops dogfood (partial of
/// <see cref="AgentOpsDogfoodTests"/>): the call record with success/errorCode
/// extraction, per-task tool statistics and the temp shared-parameter-file
/// scope (same pattern as AgentProjectSharedParametersTests).
/// </summary>
public sealed partial class AgentOpsDogfoodTests
{
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

    private sealed class TaskStats
    {
        private TaskStats(
            string sequence, int rounds, int maxParallel, int applyCalls, int dryRunCalls)
        {
            Sequence = sequence;
            Rounds = rounds;
            MaxParallel = maxParallel;
            ApplyCalls = applyCalls;
            DryRunCalls = dryRunCalls;
        }

        public string Sequence { get; }

        public int Rounds { get; }

        public int MaxParallel { get; }

        public int ApplyCalls { get; }

        public int DryRunCalls { get; }

        public string Describe() =>
            "tools [" + Sequence + "], rounds " + Rounds +
            ", max parallel calls/round " + MaxParallel +
            ", apply(dryRun=false) " + ApplyCalls + ", dryRun=true " + DryRunCalls;

        public static TaskStats From(
            IReadOnlyList<DogfoodCall> calls,
            IReadOnlyList<int> callsPerRound)
        {
            var sequence = calls.Count == 0
                ? "(нет вызовов)"
                : string.Join(" → ", calls.Select(call =>
                    call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

            var applyCalls = 0;
            var dryRunCalls = 0;
            foreach (var call in calls)
            {
                if (call.ToolName is not ("list_schedules" or "get_sheet_content" or
                        "list_project_parameters" or "list_families"))
                {
                    if (IsApply(call))
                        applyCalls++;
                    else
                        dryRunCalls++;
                }
            }

            var maxParallel = callsPerRound.Count == 0 ? 0 : callsPerRound.Max();
            return new TaskStats(sequence, callsPerRound.Count, maxParallel, applyCalls, dryRunCalls);
        }

        private static bool IsApply(DogfoodCall call)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                return document.RootElement.TryGetProperty("dryRun", out var value)
                       && value.ValueKind == JsonValueKind.False;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

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
            var directory = Path.Combine(Path.GetTempPath(), "SmartConOpsDogfood");
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
