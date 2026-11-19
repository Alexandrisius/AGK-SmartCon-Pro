using System.Globalization;
using System.IO;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Jobs;

namespace SmartCon.Revit.Agent.Tools.Jobs;

/// <summary>
/// Per-item report with an independent read-back: the operator sees what actually
/// changed, what was skipped, what failed and what no longer matches the model.
/// </summary>
public sealed class GetJobReportTool : IAgentTool
{
    private readonly IJobStore _store;
    private readonly JobReportBuilder _builder;
    private readonly IRevitContext _revitContext;

    public GetJobReportTool(IJobStore store, JobReportBuilder builder, IRevitContext revitContext)
    {
        _store = store;
        _builder = builder;
        _revitContext = revitContext;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_job_report",
        Description =
            "Returns the per-item report of a job: expected/actual/verified per unit, saved state, " +
            "failures and mismatches. format=csv writes a CSV file and returns its path. " +
            "Omit jobId for the most recent job of the active document.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = true,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "report", "diff", "csv", "verified", "mismatch" },
        TimeoutMs = 120000,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""jobId"":{""type"":""string""},""format"":{""type"":""string"",""enum"":[""json"",""csv""]},""offset"":{""type"":""integer"",""minimum"":0},""limit"":{""type"":""integer"",""minimum"":1,""maximum"":5000},""readBack"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var (jobId, resolveError) = await JobToolResolution
            .ResolveAsync(_store, args, context.DocumentId, ct)
            .ConfigureAwait(false);
        if (resolveError is not null)
        {
            return resolveError;
        }

        if (jobId is null)
        {
            return ToolResult.Fail(
                JobErrorCodes.JobNotFound,
                "No job found for this document. [Action: call get_job_status to list recent jobs]");
        }

        var format = AgentJson.GetString(args, "format") ?? "json";
        var offset = AgentJson.GetInt(args, "offset", 0);
        var limit = Math.Min(Math.Max(AgentJson.GetInt(args, "limit", 100), 1), 5000);
        var readBack = AgentJson.GetBool(args, "readBack", true);

        JobReport? report;
        JobError? buildError = null;
        try
        {
            report = await context.Revit.RaiseAsync(
                _ => _builder.Build(
                    _revitContext.GetDocument(),
                    jobId.Value,
                    format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? 0 : offset,
                    format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? int.MaxValue : limit,
                    readBack,
                    out buildError),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "job_report_failed",
                ex.GetType().Name + ": " + ex.Message +
                " [Action: make sure the job's document is open, then retry]");
        }

        if (report is null)
        {
            return ToolResult.Fail(
                buildError?.Code ?? JobErrorCodes.JobNotFound,
                buildError?.ToMessage() ?? "Job '" + jobId.Value.ToString("D") + "' was not found");
        }

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = _builder.ToCsv(report);
            var path = WriteCsv(report.JobId, csv);
            return ToolResult.Ok(
                JobToolPayloads.Serialize(new
                {
                    jobId = report.JobId.ToString("D"),
                    csvPath = path,
                    rows = report.Items.Count,
                    state = report.State.ToString()
                }),
                "CSV report written: " + path + " (" + report.Items.Count + " rows)",
                affectedCount: report.Items.Count);
        }

        var payload = new
        {
            jobId = report.JobId.ToString("D"),
            action = report.Action,
            state = report.State.ToString(),
            saved = report.Saved,
            volatileJob = report.Volatile,
            lastSavedCursor = report.LastSavedCursor,
            lastSavedAt = report.LastSavedAt,
            totals = report.Totals,
            truthful = report.Truthful,
            mismatchCount = report.MismatchCount,
            warnings = report.Warnings,
            errorCode = report.ErrorCode,
            errorDetail = report.ErrorDetail,
            offset,
            limit,
            hasMore = report.HasMore,
            nextOffset = report.NextOffset,
            items = report.Items
        };

        var message = "Report: " + report.Totals.Done + " done, " + report.Totals.Skipped +
                      " skipped, " + report.Totals.Failed + " failed, " + report.Totals.Mismatch +
                      " mismatch" + (report.Saved ? ", saved" : ", NOT saved") +
                      (report.Truthful ? ", read-back verified" : ", READ-BACK MISMATCH");

        return ToolResult.Ok(
            JobToolPayloads.Serialize(payload),
            message,
            affectedCount: report.Items.Count,
            warnings: report.Warnings.Count == 0 ? null : report.Warnings);
    }

    private static string WriteCsv(Guid jobId, string csv)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartCon", "Agent", "reports");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(
            directory,
            jobId.ToString("D") + "-" +
            DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".csv");
        File.WriteAllText(path, csv, System.Text.Encoding.UTF8);
        return path;
    }
}