using Autodesk.Revit.UI;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Core.Services.Interfaces;
#if REVIT2023_OR_GREATER
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Transactions;
#endif

namespace SmartCon.App.DI;

/// <summary>Job-engine registration: store, runner, actions, pump and tools.</summary>
public static partial class ServiceRegistrar
{
#if REVIT2023_OR_GREATER
    private static void RegisterJobEngine(
        IServiceCollection services,
        IRevitContext revitContext,
        AgentDatabase agentDatabase)
    {
        // One active job per document; the pump runs on its own ExternalEvent so a
        // long batch never blocks agent tool calls and vice versa.
        var jobStore = new SqliteAgentJobStore(agentDatabase);
        var jobActions = new IJobAction[]
        {
            new RenameViewsJobAction(),
            new SetParametersJobAction(),
            new MapFamilyParametersJobAction(),
            new ReplaceFamilyParameterJobAction()
        };
        var jobRunner = new JobRunner(jobStore, jobActions, new RevitTransactionService(revitContext));
        services.AddSingleton<IJobStore>(jobStore);
        services.AddSingleton(jobRunner);
        services.AddSingleton<IJobScheduler>(jobRunner);
        services.AddSingleton<IJobRecovery>(jobRunner);
        services.AddSingleton(sp => new JobReportBuilder(
            sp.GetRequiredService<IJobStore>(),
            jobActions));

        var jobPumpHandler = new JobPumpEventHandler(jobRunner);
        var jobPumpEvent = ExternalEvent.Create(jobPumpHandler);
        jobPumpHandler.Attach(jobPumpEvent);
        jobRunner.Initialize(() => jobPumpEvent.Raise());
        services.AddSingleton(jobPumpHandler);

        services.AddSingleton<IAgentTool>(sp => new StartJobTool(
            sp.GetRequiredService<IJobScheduler>(),
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<IRevitContext>()));

        services.AddSingleton<IAgentTool>(sp => new GetJobStatusTool(
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<IJobScheduler>()));
        services.AddSingleton<IAgentTool>(sp => new CancelJobTool(
            sp.GetRequiredService<IJobScheduler>(),
            sp.GetRequiredService<IJobStore>()));
        services.AddSingleton<IAgentTool>(sp => new GetJobReportTool(
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<JobReportBuilder>(),
            sp.GetRequiredService<IRevitContext>()));
        services.AddSingleton<IAgentTool>(sp => new RollbackJobTool(
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<IJobRecovery>(),
            sp.GetRequiredService<IRevitContext>()));
        services.AddSingleton<IAgentTool>(sp => new RetryFailedItemsTool(
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<IJobRecovery>(),
            sp.GetRequiredService<IRevitContext>()));
    }

#endif
}
