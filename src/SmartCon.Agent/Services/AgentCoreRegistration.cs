using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Storage;
using SmartCon.Agent.Services.Tools;
using SmartCon.Agent.Services.Tools.Read;
using SmartCon.Agent.Services.Tools.Semantic;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent;

/// <summary>Inputs that only the composition root (application or test host) can provide.</summary>
public sealed class AgentCoreOptions
{
    public required AgentLlmSettings Settings { get; init; }

    public required AgentDatabase Database { get; init; }

    public required IAgentAwaitableEvent AwaitableEvent { get; init; }

    public required IRevitContext RevitContext { get; init; }
}

/// <summary>
/// Single source of truth for the Agent-side production registrations: LLM
/// client, SQLite stores, Agent-side tools, decisions, tool registry,
/// executor, prompt, compaction and orchestrator. The application DI and the
/// integration-test production agent host both compose the graph from this
/// method, so the benchmark runs the same loop the application runs.
/// Registration order is production order and must stay stable.
/// </summary>
public static class AgentCoreRegistration
{
    /// <summary>
    /// Production agent services: LLM client, stores, Agent-side tools,
    /// decisions, registry, executor, prompt, compaction, orchestrator.
    /// UI-only registrations (file picker, clipboard, view model, pane) stay
    /// in the application; the job-engine pump is attached by the caller of
    /// <c>AgentToolRegistration.AddAgentJobEngine</c>.
    /// </summary>
    public static IServiceCollection AddAgentCore(
        this IServiceCollection services,
        AgentCoreOptions options)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (options is null) throw new ArgumentNullException(nameof(options));

        services.AddSingleton(options.Settings);
        services.AddSingleton<IAgentLlmClient>(sp =>
            new OpenAiCompatibleLlmClient(sp.GetRequiredService<AgentLlmSettings>()));
        services.AddSingleton(options.Database);
        services.AddSingleton<IAgentConversationStore, SqliteAgentConversationStore>();
        services.AddSingleton<IAgentAuditLog, SqliteAgentAuditLog>();
        services.AddSingleton<IAgentChangeSetStore, SqliteAgentChangeSetStore>();
        services.AddSingleton<IAgentToolCallStore, SqliteAgentToolCallStore>();
        services.AddSingleton<IAgentUsageStore, SqliteAgentUsageStore>();
        services.AddSingleton<IAgentWorkingStateStore, SqliteAgentWorkingStateStore>();
        services.AddSingleton<IAgentToolResultBlobStore, SqliteAgentToolResultBlobStore>();
        services.AddSingleton<IAgentCompactionStore, SqliteAgentCompactionStore>();
        services.AddSingleton<IAgentFilterQuarantineStore, SqliteAgentFilterQuarantineStore>();
        services.AddSingleton<IAgentRuntimeInfo, AgentRuntimeInfo>();
        services.AddSingleton<IAgentAwaitableEvent>(options.AwaitableEvent);
        if (!services.Any(d => d.ServiceType == typeof(IRevitContext)))
            services.AddSingleton<IRevitContext>(options.RevitContext);

        // ListKnownModelsTool reads agent.db (Agent storage), so it cannot be
        // registered from SmartCon.Revit; production registered it inside the
        // Revit tool block, here it keeps its place ahead of the other
        // Agent-side tools so the catalog set stays identical.
        services.AddSingleton<IAgentTool, ListKnownModelsTool>();
        services.AddSingleton<IAgentTool>(sp => new ExecutePlanTool(
            () => sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<IAgentAwaitableEvent>(),
            sp.GetRequiredService<IAgentAuditLog>(),
            sp.GetRequiredService<AgentSessionContext>()));

        services.AddSingleton<AgentConfirmationService>();
        services.AddSingleton<IAgentConfirmationService>(
            sp => sp.GetRequiredService<AgentConfirmationService>());

        services.AddSingleton<AgentSessionContext>();
        services.AddSingleton<FindToolsTool>();
        services.AddSingleton<IAgentTool>(sp => sp.GetRequiredService<FindToolsTool>());
        services.AddSingleton<LoadToolPackTool>();
        services.AddSingleton<IAgentTool>(sp => sp.GetRequiredService<LoadToolPackTool>());
        services.AddSingleton<ReadToolResultTool>();
        services.AddSingleton<IAgentTool>(sp => sp.GetRequiredService<ReadToolResultTool>());

        var decisionsSettings = AgentDecisionsSettings.Load();
        services.AddSingleton(decisionsSettings);
        services.AddSingleton<DecisionCostTracker>();
        services.AddSingleton<IAgentDecisionClient>(sp =>
            new HttpDecisionClient(
                sp.GetRequiredService<AgentDecisionsSettings>(),
                costTracker: sp.GetRequiredService<DecisionCostTracker>()));
        // One ranker for the whole graph, carrying the per-model question
        // budget (S.49, #534) into classify_types and semantic matching.
        services.AddSingleton(sp => new AgentSemanticRanker(
                sp.GetRequiredService<IAgentDecisionClient>())
        {
            MaxQuestionsPerToolCall =
                SmartCon.Core.Services.Agent.AgentClassificationBudget.Normalize(
                    sp.GetRequiredService<AgentDecisionsSettings>().MaxQuestionsPerToolCall)
        });
        services.AddSingleton(sp =>
        {
            // Always wrapped: the decision router falls back to BM25 when the
            // decision layer is disabled at startup, and picks the model chosen in
            // the settings UI immediately (no restart).
            return new DecisionsToolRouter(
                new RuleToolRouter(), sp.GetRequiredService<IAgentDecisionClient>());
        });
        services.AddSingleton<IToolRouter>(sp => sp.GetRequiredService<DecisionsToolRouter>());

        services.AddSingleton<IToolRegistry>(sp => new AgentToolRegistry(
            sp.GetServices<IAgentTool>(),
            sp.GetRequiredService<FindToolsTool>(),
            sp.GetRequiredService<LoadToolPackTool>()));
        services.AddSingleton<IAgentTool>(sp => new SemanticMatchTool(
            sp.GetRequiredService<IAgentDecisionClient>(),
            sp.GetRequiredService<AgentDecisionsSettings>()));
        services.AddSingleton<IAgentTool, ReadClassifierTool>();
        services.AddSingleton<IToolExecutor, AgentToolExecutor>();
        services.AddSingleton<AgentPromptBuilder>();
        services.AddSingleton<IAgentSummarizer>(sp => new LlmAgentSummarizer(
            sp.GetRequiredService<IAgentLlmClient>(),
            sp.GetRequiredService<AgentLlmSettings>()));
        services.AddSingleton<IAgentContextCompactor>(sp => new AgentContextCompactor(
            sp.GetRequiredService<IAgentSummarizer>(),
            sp.GetRequiredService<AgentLlmSettings>(),
            sp.GetRequiredService<IAgentCompactionStore>()));
        services.AddSingleton<AgentOrchestrator>();
        return services;
    }

    /// <summary>
    /// Agent-side tool types registered here (for the registration-coverage
    /// guard in <c>SmartCon.Tests</c>). Keep in sync with the method above.
    /// </summary>
    public static IReadOnlyList<Type> ToolTypes { get; } = new[]
    {
        typeof(ListKnownModelsTool),
        typeof(ExecutePlanTool),
        typeof(FindToolsTool),
        typeof(LoadToolPackTool),
        typeof(ReadToolResultTool),
        typeof(SemanticMatchTool),
        typeof(ReadClassifierTool)
    };
}
