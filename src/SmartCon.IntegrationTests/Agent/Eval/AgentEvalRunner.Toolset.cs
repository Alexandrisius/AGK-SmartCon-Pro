using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Eval;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent.Eval;

internal sealed partial class AgentEvalRunner
{
    private List<IAgentTool> CreateToolset(
        Document document,
        RevitTransactionService transactions,
        out InMemoryAgentChangeSetStore changeSets)
    {
        var context = new StubRevitContext(document);
        changeSets = new InMemoryAgentChangeSetStore();
        return new List<IAgentTool>
        {
            new GetProjectOverviewTool(context),
            new ListViewsTool(context),
            new ListLevelsTool(context),
            new ListCategoriesTool(context),
            new QueryElementsTool(context),
            new GetElementDetailsTool(context),
            new SummarizeElementsTool(context),
            new ListViewTemplatesTool(context),
            new GetViewSettingsTool(context),
            new GetViewTemplateDetailsTool(context),
            new GetCurrentViewInfoTool(context),
            new ListSheetsTool(context),
            new GetSheetContentTool(context),
            new ListSchedulesTool(context),
            new GetScheduleDataTool(context),
            new RenameViewsTool(context, transactions, changeSets),
            new RenameLevelsTool(context, transactions, changeSets),
            new ApplyViewTemplateTool(context, transactions, changeSets),
            new SetViewRangeTool(context, transactions, changeSets),
            new SetViewCropTool(context, transactions, changeSets),
            new SetViewVisibilityTool(context, transactions, changeSets),
            new ConfigureViewsTool(context, transactions, changeSets),
            new CopyViewSettingsTool(context, transactions, changeSets),
            new CreateViewFilterTool(context, transactions, changeSets),
            new SetFilterOverridesTool(context, transactions, changeSets),
            new SetParametersTool(context, transactions, changeSets),
            new RemapParameterValuesTool(context, transactions, changeSets),
            new DiscardChangeSetTool(context, changeSets),
            new UndoLastTool(context, transactions, changeSets)
        }.ToList();
    }

    private static async Task ApplyRoutingAsync(
        AgentEvalTask task,
        string prompt,
        IReadOnlyList<IAgentTool> toolset,
        AgentEvalTaskResult result)
    {
        var definitions = toolset
            .Select(tool => AgentToolCatalog.Enrich(tool.Definition))
            .ToList();
        var decision = await new RuleToolRouter().RouteAsync(prompt, definitions);
        result.RoutingFallback = decision.Fallback;

        foreach (var expected in task.ExpectedTools)
        {
            if (decision.Tools.Any(tool =>
                    string.Equals(tool.Name, expected, StringComparison.Ordinal)))
            {
                continue;
            }

            if (task.AlternativeTools.Count > 0
                && decision.Tools.Any(tool =>
                    task.AlternativeTools.Contains(tool.Name, StringComparer.Ordinal)))
            {
                continue;
            }

            result.RoutingHit = false;
            break;
        }
    }

    private static string SystemRules(AgentEvalTask task)
    {
        switch (task.Kind)
        {
            case AgentEvalTaskKind.WriteDryRun:
                return "- This is an evaluation run. The user asks for a change but only the " +
                       "dry run is allowed: never call a write tool with dryRun=false.\n" +
                       "- Finish with a short plan: what changes, how many items.";
            case AgentEvalTaskKind.WriteApplyUndo:
                return "- This is an evaluation run. Produce the dry-run plan for the requested " +
                       "change first; the user approves applying it afterwards.";
            default:
                return "- This is an evaluation run. Use the tools to read the model and answer " +
                       "the question in Russian; do not propose changes.";
        }
    }
}
