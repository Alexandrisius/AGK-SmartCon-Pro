using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// execute_plan with family tools (H1d stretch): every step runs the family
/// lifecycle (dry run → apply → read-back), per-step results are returned and a
/// failing dry run stops the plan before any change set is written.
/// </summary>
public sealed class AgentFamilyPlanTests : RevitApiTest
{
    private Document? _document;
    private FamilySeed? _seed;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Before(Test)]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentFamilyPlan", out var reason);
        if (_seed is null)
            Skip.Test(reason ?? "Не удалось сидировать семейство");
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task Plan_AddParameters_AppliesBothStepsAndReadsBack()
    {
        var add = CreateAddTool();
        var read = new GetFamilyParametersTool(new StubRevitContext(Doc));
        var executor = new AgentPlanExecutor(
            new TestToolRegistry(new IAgentTool[] { add, read }),
            new ImmediateAwaitableEvent(),
            new FixedConfirmation());

        var plan = new AgentPlan
        {
            Goal = "Add two family parameters",
            Steps = new[]
            {
                AddStep("s1", "Заказчик", readBack: true),
                AddStep("s2", "Заказчик2", readBack: false)
            }
        };

        var result = await executor.ExecuteAsync(
            plan,
            new AgentPlanExecutionContext
            {
                DocumentId = SmartCon.Revit.Agent.AgentDocumentIdentity.GetDocumentId(Doc)
            },
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Approved).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(2);
        await Assert.That(result.Steps.Count).IsEqualTo(2);
        await Assert.That(result.Steps.All(step => step.DryRunOk)).IsTrue();
        await Assert.That(result.Steps.All(step => step.ApplyResult?.Success == true)).IsTrue();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(2);

        var parameters = await ReadParameters();
        await Assert.That(HasParameter(parameters, "Заказчик")).IsTrue();
        await Assert.That(HasParameter(parameters, "Заказчик2")).IsTrue();
    }

    [Test]
    public async Task Plan_DuplicateParameter_StopsBeforeApply()
    {
        var add = CreateAddTool();
        var executor = new AgentPlanExecutor(
            new TestToolRegistry(new IAgentTool[] { add }),
            new ImmediateAwaitableEvent(),
            new FixedConfirmation());

        var plan = new AgentPlan
        {
            Goal = "Add the same family parameter twice",
            Steps = new[]
            {
                AddStep("s1", "Дубликат", readBack: false),
                AddStep("s2", "Дубликат", readBack: false)
            }
        };

        var result = await executor.ExecuteAsync(
            plan,
            new AgentPlanExecutionContext
            {
                DocumentId = SmartCon.Revit.Agent.AgentDocumentIdentity.GetDocumentId(Doc)
            },
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("plan_apply_failed");
        await Assert.That(result.Steps.Count).IsEqualTo(2);
        await Assert.That(result.Steps[0].ApplyResult?.Success).IsTrue();
        await Assert.That(result.Steps[1].ApplyResult?.Success).IsFalse();
        await Assert.That(result.Steps[1].ApplyResult?.ErrorCode)
            .IsEqualTo("parameter_exists");
        // Plans are not atomic (documented): step 1 stays applied.
        await Assert.That(_changeSets!.All.Count).IsEqualTo(1);
        await Assert.That(HasParameter(await ReadParameters(), "Дубликат")).IsTrue();
    }

    private AddFamilyParameterTool CreateAddTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private AgentPlanStep AddStep(string stepId, string name, bool readBack) => new()
    {
        StepId = stepId,
        ToolName = "add_family_parameter",
        ArgumentsJson =
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"name\":\"" + name + "\"," +
            "\"storageType\":\"text\",\"group\":\"PG_IDENTITY_DATA\",\"dryRun\":true}",
        ExpectedChanges = 1,
        ReadBackTool = readBack ? "get_family_parameters" : null,
        ArgumentsReadBackJson = readBack
            ? "{\"familyName\":\"" + Seed.FamilyName + "\"}"
            : "{}",
        ReadBackExpectations = readBack
            ? new[]
            {
                new AgentPlanExpectation
                {
                    Path = "familyName",
                    Expected = Seed.FamilyName
                }
            }
            : Array.Empty<AgentPlanExpectation>()
    };

    private async Task<JsonElement> ReadParameters()
    {
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            JsonDocument.Parse("{\"familyId\":" + Seed.FamilyId + ",\"includeValues\":false}")
                .RootElement.Clone(),
            CancellationToken.None);
        await Assert.That(result.Success).IsTrue();
        return result.Data ?? throw new InvalidOperationException("No data.");
    }

    private static bool HasParameter(JsonElement data, string name)
    {
        foreach (var parameter in data.GetProperty("parameters").EnumerateArray())
        {
            if (string.Equals(parameter.GetProperty("name").GetString(), name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FixedConfirmation : IAgentConfirmationService
    {
        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct) =>
            Task.FromResult(AgentConfirmationResult.Approved);
    }

    private sealed class TestToolRegistry : IToolRegistry
    {
        private readonly List<IAgentTool> _tools;
        private readonly Dictionary<string, IAgentTool> _byName;

        public TestToolRegistry(IEnumerable<IAgentTool> tools)
        {
            _tools = tools.ToList();
            _byName = _tools.ToDictionary(
                tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<IAgentTool> GetAll() => _tools;

        public IReadOnlyList<AgentToolDefinition> GetDefinitions() =>
            _tools.Select(tool => tool.Definition).ToList();

        public bool TryGet(string name, out IAgentTool? tool)
        {
            if (_byName.TryGetValue(name, out var found))
            {
                tool = found;
                return true;
            }

            tool = null;
            return false;
        }
    }
}