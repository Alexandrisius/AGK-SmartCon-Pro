using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Classification;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Transactions;

namespace SmartCon.Revit.Agent;

/// <summary>
/// Single source of truth for the Revit-side production agent registrations.
/// Both the application DI (<c>SmartCon.App/DI/ServiceRegistrar</c>) and the
/// integration-test production agent host compose the graph from these methods,
/// so the benchmark catalog can never drift from what the application runs.
/// Registration order is production order and must stay stable: the tool
/// registry preserves it and the catalog/prompt rely on it.
/// </summary>
public static class AgentToolRegistration
{
    /// <summary>
    /// Every Revit-side production agent tool, registered exactly as the
    /// application registers it. Call after <see cref="AddAgentJobEngine"/>:
    /// the job tools join the catalog first, exactly as in the application.
    /// </summary>
    public static IServiceCollection AddRevitAgentTools(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.AddSingleton<IAgentTool, GetProjectOverviewTool>();
        services.AddSingleton<IAgentTool, ListViewsTool>();
        services.AddSingleton<IAgentTool, ListLevelsTool>();
        services.AddSingleton<IAgentTool, ListCategoriesTool>();
        services.AddSingleton<IAgentTool, GetSelectedElementsTool>();
        services.AddSingleton<IAgentTool, QueryElementsTool>();
        services.AddSingleton<IAgentTool, GetElementDetailsTool>();
        services.AddSingleton<IAgentTool, SummarizeElementsTool>();
        services.AddSingleton<IAgentTool, GroupFamiliesBySharedParametersTool>();
        services.AddSingleton<IAgentTool, AuditFillRateTool>();
        services.AddSingleton<IAgentTool, ListTypeParameterCandidatesTool>();
        services.AddSingleton<IAgentTool, GetElementRelationsTool>();
        services.AddSingleton<IAgentTool, TraceSystemChainTool>();
        services.AddSingleton<IAgentTool, GetRoomContentsTool>();
        services.AddSingleton<IAgentTool, ListViewTemplatesTool>();
        services.AddSingleton<IAgentTool, GetViewSettingsTool>();
        services.AddSingleton<IAgentTool, GetViewTemplateDetailsTool>();
        services.AddSingleton<IAgentTool, GetCurrentViewInfoTool>();
        services.AddSingleton<IAgentTool, ListSheetsTool>();
        services.AddSingleton<IAgentTool, GetSheetContentTool>();
        services.AddSingleton<IAgentTool, ListSchedulesTool>();
        services.AddSingleton<IAgentTool, GetScheduleDataTool>();
        services.AddSingleton<IAgentTool, GetScheduleDefinitionTool>();
        services.AddSingleton<IAgentTool, ListFamiliesTool>();
        services.AddSingleton<IAgentTool, ListFamilyTypesTool>();
        services.AddSingleton<IAgentTool, GetFamilyParametersTool>();
        services.AddSingleton<IAgentTool, FindFamiliesWithParameterTool>();
        services.AddSingleton<IAgentTool, ListSharedParametersTool>();
        services.AddSingleton<IAgentTool, ListProjectParametersTool>();
        services.AddSingleton<IAgentTool, ListFiltersTool>();
        services.AddSingleton<IAgentTool, GetFilterDetailsTool>();
        services.AddSingleton<IAgentTool, ListFilterableParametersTool>();
        services.AddSingleton<IAgentTool, ListSystemTypesTool>();
        services.AddSingleton<IAgentTool, ListMepSystemsTool>();
        services.AddSingleton<IAgentTool, GetBrowserOrganizationTool>();
        services.AddSingleton<IAgentTool, ListMaterialsTool>();
        services.AddSingleton<IAgentTool, ListScopeBoxesTool>();
        services.AddSingleton<IAgentTool, RenameViewsTool>();
        services.AddSingleton<IAgentTool, RenameLevelsTool>();
        services.AddSingleton<IAgentTool, ApplyViewTemplateTool>();
        services.AddSingleton<IAgentTool, SetViewTemplateInclusionTool>();
        services.AddSingleton<IAgentTool, SetViewRangeTool>();
        services.AddSingleton<IAgentTool, SetViewCropTool>();
        services.AddSingleton<IAgentTool, SetViewVisibilityTool>();
        services.AddSingleton<IAgentTool, ConfigureViewsTool>();
        services.AddSingleton<IAgentTool, CopyViewSettingsTool>();
        services.AddSingleton<IAgentTool, CopyCropFromViewTool>();
        services.AddSingleton<IAgentTool, AssignScopeBoxTool>();
        services.AddSingleton<IAgentTool, CreateViewFilterTool>();
        services.AddSingleton<IAgentTool, SetFilterRulesTool>();
        services.AddSingleton<IAgentTool, RenameFilterTool>();
        services.AddSingleton<IAgentTool, DeleteFilterTool>();
        services.AddSingleton<IAgentTool, MergeDuplicateFiltersTool>();
        services.AddSingleton<IAgentTool, SetViewParametersTool>();
        services.AddSingleton<IAgentTool, DeleteViewsTool>();
        services.AddSingleton<IAgentTool, DuplicateViewTool>();
        services.AddSingleton<IAgentTool, CreateViewTool>();
        services.AddSingleton<IAgentTool, CreateFloorPlansTool>();
        services.AddSingleton<IAgentTool, CreateViewsBatchTool>();
        services.AddSingleton<IAgentTool, SetBrowserFolderTool>();
        services.AddSingleton<IAgentTool, DeleteSheetsTool>();
        services.AddSingleton<IAgentTool, DeleteElementsTool>();
        services.AddSingleton<IAgentTool, DeleteSchedulesTool>();
        services.AddSingleton<IAgentTool, CreateScheduleTool>();
        services.AddSingleton<IAgentTool, AddScheduleFieldTool>();
        services.AddSingleton<IAgentTool, SetScheduleFilterTool>();
        services.AddSingleton<IAgentTool, SetScheduleSortTool>();
        services.AddSingleton<IAgentTool, PlaceScheduleOnSheetTool>();
        services.AddSingleton<IAgentTool, RemoveScheduleFieldTool>();
        services.AddSingleton<IAgentTool, SetScheduleFieldOrderTool>();
        services.AddSingleton<IAgentTool, SetScheduleFieldVisibilityTool>();
        services.AddSingleton<IAgentTool, SetScheduleTotalsTool>();
        services.AddSingleton<IAgentTool, MoveScheduleOnSheetTool>();
        services.AddSingleton<IAgentTool, RenameScheduleTool>();
        services.AddSingleton<IAgentTool, SetScheduleFieldFormatTool>();
        services.AddSingleton<IAgentTool, SetScheduleStyleTool>();
        services.AddSingleton<IAgentTool, RemoveScheduleFromSheetTool>();
        services.AddSingleton<IAgentTool, CloneScheduleTool>();
        services.AddSingleton<IAgentTool, GetTitleblockOptionsTool>();
        services.AddSingleton<IAgentTool, GetViewPlacementPreviewTool>();
        services.AddSingleton<IAgentTool, BuildSystemSheetsTool>();
        services.AddSingleton<IAgentTool, OpenModelBackgroundTool>();
        services.AddSingleton<IAgentTool, CloseModelTool>();
        services.AddSingleton<IAgentTool, SetMepSystemTypeTool>();
        services.AddSingleton<IAgentTool, CreateSheetTool>();
        services.AddSingleton<IAgentTool, CreateViewportsTool>();
        services.AddSingleton<IAgentTool, MoveViewportTool>();
        services.AddSingleton<IAgentTool, MoveViewportsTool>();
        services.AddSingleton<IAgentTool, RemoveViewportsTool>();
        services.AddSingleton<IAgentTool, SetSheetNumberTool>();
        services.AddSingleton<IAgentTool, RenumberSheetsTool>();
        services.AddSingleton<IAgentTool, RenameSheetTool>();
        services.AddSingleton<IAgentTool, SanitizeSheetNumbersTool>();
        services.AddSingleton<IAgentTool, DeleteFamilyTypesTool>();
        services.AddSingleton<IAgentTool, RemoveFamilyParameterTool>();
        services.AddSingleton<IAgentTool, ReplaceFamilyParameterTool>();
        services.AddSingleton<IAgentTool, SetFilterOverridesTool>();
        services.AddSingleton<IAgentTool, SetParametersTool>();
        services.AddSingleton<IAgentTool, RemapParameterValuesTool>();
        services.AddSingleton<IAgentTool, AddProjectSharedParameterTool>();
        services.AddSingleton<IAgentTool, RenameFamilyParameterTool>();
        services.AddSingleton<IAgentTool, AddFamilyParameterTool>();
        services.AddSingleton<IAgentTool, SetFamilyParameterValuesTool>();
        services.AddSingleton<IAgentTool, SetFamilyFormulaTool>();
        services.AddSingleton<IAgentTool, MapFamilyParametersTool>();
        services.AddSingleton<IAgentTool, DuplicateFamilyTypeTool>();
        services.AddSingleton<IAgentTool, RenameFamilyTypeTool>();
        services.AddSingleton<IAgentTool, ReplaceFamilyTypeTool>();
        services.AddSingleton<IAgentTool, LoadFamilyTool>();
        services.AddSingleton<IAgentTool, PurgeFamiliesTool>();
        services.AddSingleton<IAgentTool, DiscardChangeSetTool>();
        // Concrete registration: undo_until takes the undo_last instance
        // directly (shared revert seam). Without it the container cannot
        // activate UndoUntilTool — the hole existed since #352 and was masked
        // by the pane-registration try/catch in the application.
        services.AddSingleton<UndoLastTool>();
        // Forward to the same concrete instance: a separate
        // AddSingleton<IAgentTool, UndoLastTool>() would create a SECOND
        // instance, and the catalog copy would diverge from the revert seam.
        services.AddSingleton<IAgentTool>(sp => sp.GetRequiredService<UndoLastTool>());
        services.AddSingleton<IAgentTool, UndoUntilTool>();
        services.AddSingleton<IAgentTool, SaveDocumentTool>();
        services.AddSingleton<IAgentTool, ListChangeSetsTool>();
        services.AddSingleton<IAgentTool, GetChangeSetDetailsTool>();
        services.AddSingleton<IAgentTool, AuditModelTool>();
        services.AddSingleton<IAgentTool, ListClassificationEntriesTool>();
        services.AddSingleton<IAgentTool, LoadClassificationTableTool>();
        // The factory resolves the decision client lazily: AddAgentCore has
        // registered it by the time the Revit tool graph is composed.
        services.AddSingleton<IAgentTool>(sp => new ClassifyTypesTool(
            sp.GetRequiredService<IRevitContext>(),
            new AgentSemanticRanker(sp.GetRequiredService<IAgentDecisionClient>())));

        // Not an IAgentTool, but Revit-side agent wiring: registered with the
        // tools so the whole Revit half of the agent graph lives here.
        services.AddSingleton<IAgentProjectContextProvider, AgentProjectContextProvider>();
        return services;
    }

    /// <summary>The production set of job-engine actions.</summary>
    public static IJobAction[] CreateJobActions() => new IJobAction[]
    {
        new RenameViewsJobAction(),
        new SetParametersJobAction(),
        new MapFamilyParametersJobAction(),
        new ReplaceFamilyParameterJobAction()
    };

    /// <summary>
    /// Shared job-engine wiring (store, runner, actions, report builder, job
    /// tools). The runner is returned NOT yet initialized: the caller attaches
    /// its pump — the application creates an ExternalEvent, the integration-test
    /// host runs the slices inline on the Revit API thread.
    /// </summary>
    public static JobRunner AddAgentJobEngine(
        this IServiceCollection services,
        IRevitContext revitContext,
        IJobStore jobStore)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        // One active job per document; the pump runs on its own ExternalEvent so a
        // long batch never blocks agent tool calls and vice versa.
        var jobActions = CreateJobActions();
        var jobRunner = new JobRunner(jobStore, jobActions, new RevitTransactionService(revitContext));
        services.AddSingleton<IJobStore>(jobStore);
        services.AddSingleton(jobRunner);
        services.AddSingleton<IJobScheduler>(jobRunner);
        services.AddSingleton<IJobRecovery>(jobRunner);
        services.AddSingleton(sp => new JobReportBuilder(
            sp.GetRequiredService<IJobStore>(),
            jobActions));

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
        return jobRunner;
    }

    /// <summary>
    /// Tool types registered by this class (simple registrations and
    /// factory-created ones), for the registration-coverage guard in
    /// <c>SmartCon.Tests</c>. Keep in sync with the methods above.
    /// </summary>
    public static IReadOnlyList<Type> ToolTypes { get; } = new[]
    {
        typeof(GetProjectOverviewTool),
        typeof(ListViewsTool),
        typeof(ListLevelsTool),
        typeof(ListCategoriesTool),
        typeof(GetSelectedElementsTool),
        typeof(QueryElementsTool),
        typeof(GetElementDetailsTool),
        typeof(SummarizeElementsTool),
        typeof(GroupFamiliesBySharedParametersTool),
        typeof(AuditFillRateTool),
        typeof(ListTypeParameterCandidatesTool),
        typeof(GetElementRelationsTool),
        typeof(TraceSystemChainTool),
        typeof(GetRoomContentsTool),
        typeof(ListViewTemplatesTool),
        typeof(GetViewSettingsTool),
        typeof(GetViewTemplateDetailsTool),
        typeof(GetCurrentViewInfoTool),
        typeof(ListSheetsTool),
        typeof(GetSheetContentTool),
        typeof(ListSchedulesTool),
        typeof(GetScheduleDataTool),
        typeof(GetScheduleDefinitionTool),
        typeof(ListFamiliesTool),
        typeof(ListFamilyTypesTool),
        typeof(GetFamilyParametersTool),
        typeof(FindFamiliesWithParameterTool),
        typeof(ListSharedParametersTool),
        typeof(ListProjectParametersTool),
        typeof(ListFiltersTool),
        typeof(GetFilterDetailsTool),
        typeof(ListFilterableParametersTool),
        typeof(ListSystemTypesTool),
        typeof(ListMepSystemsTool),
        typeof(GetBrowserOrganizationTool),
        typeof(ListMaterialsTool),
        typeof(ListScopeBoxesTool),
        typeof(RenameViewsTool),
        typeof(RenameLevelsTool),
        typeof(ApplyViewTemplateTool),
        typeof(SetViewTemplateInclusionTool),
        typeof(SetViewRangeTool),
        typeof(SetViewCropTool),
        typeof(SetViewVisibilityTool),
        typeof(ConfigureViewsTool),
        typeof(CopyViewSettingsTool),
        typeof(CopyCropFromViewTool),
        typeof(AssignScopeBoxTool),
        typeof(CreateViewFilterTool),
        typeof(SetFilterRulesTool),
        typeof(RenameFilterTool),
        typeof(DeleteFilterTool),
        typeof(MergeDuplicateFiltersTool),
        typeof(SetViewParametersTool),
        typeof(DeleteViewsTool),
        typeof(DuplicateViewTool),
        typeof(CreateViewTool),
        typeof(CreateFloorPlansTool),
        typeof(CreateViewsBatchTool),
        typeof(SetBrowserFolderTool),
        typeof(DeleteSheetsTool),
        typeof(DeleteElementsTool),
        typeof(DeleteSchedulesTool),
        typeof(CreateScheduleTool),
        typeof(AddScheduleFieldTool),
        typeof(SetScheduleFilterTool),
        typeof(SetScheduleSortTool),
        typeof(PlaceScheduleOnSheetTool),
        typeof(RemoveScheduleFieldTool),
        typeof(SetScheduleFieldOrderTool),
        typeof(SetScheduleFieldVisibilityTool),
        typeof(SetScheduleTotalsTool),
        typeof(MoveScheduleOnSheetTool),
        typeof(RenameScheduleTool),
        typeof(SetScheduleFieldFormatTool),
        typeof(SetScheduleStyleTool),
        typeof(RemoveScheduleFromSheetTool),
        typeof(CloneScheduleTool),
        typeof(GetTitleblockOptionsTool),
        typeof(GetViewPlacementPreviewTool),
        typeof(BuildSystemSheetsTool),
        typeof(OpenModelBackgroundTool),
        typeof(CloseModelTool),
        typeof(SetMepSystemTypeTool),
        typeof(CreateSheetTool),
        typeof(CreateViewportsTool),
        typeof(MoveViewportTool),
        typeof(MoveViewportsTool),
        typeof(RemoveViewportsTool),
        typeof(SetSheetNumberTool),
        typeof(RenumberSheetsTool),
        typeof(RenameSheetTool),
        typeof(SanitizeSheetNumbersTool),
        typeof(DeleteFamilyTypesTool),
        typeof(RemoveFamilyParameterTool),
        typeof(ReplaceFamilyParameterTool),
        typeof(SetFilterOverridesTool),
        typeof(SetParametersTool),
        typeof(RemapParameterValuesTool),
        typeof(AddProjectSharedParameterTool),
        typeof(RenameFamilyParameterTool),
        typeof(AddFamilyParameterTool),
        typeof(SetFamilyParameterValuesTool),
        typeof(SetFamilyFormulaTool),
        typeof(MapFamilyParametersTool),
        typeof(DuplicateFamilyTypeTool),
        typeof(RenameFamilyTypeTool),
        typeof(ReplaceFamilyTypeTool),
        typeof(LoadFamilyTool),
        typeof(PurgeFamiliesTool),
        typeof(DiscardChangeSetTool),
        typeof(UndoLastTool),
        typeof(UndoUntilTool),
        typeof(SaveDocumentTool),
        typeof(ListChangeSetsTool),
        typeof(GetChangeSetDetailsTool),
        typeof(AuditModelTool),
        typeof(ListClassificationEntriesTool),
        typeof(LoadClassificationTableTool),
        typeof(ClassifyTypesTool),
        typeof(StartJobTool),
        typeof(GetJobStatusTool),
        typeof(CancelJobTool),
        typeof(GetJobReportTool),
        typeof(RollbackJobTool),
        typeof(RetryFailedItemsTool)
    };
}
