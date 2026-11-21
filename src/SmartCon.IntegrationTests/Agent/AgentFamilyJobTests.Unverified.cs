using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Jobs;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// B.6 (#432): a family job whose targets live in the family document only
/// (instance scope) settles as CompletedUnverified — the values are applied but
/// no project-side read can prove them, which is not a failure.
/// </summary>
public sealed partial class AgentFamilyJobTests
{
    [Test]
    public async Task FamilyJob_InstanceScopeTargets_EndsCompletedUnverifiedWithRealTransfer()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();

        const string targetName = "ЦельЭкз";
        const string type1Value = "прим-Тип1";
        const string type2Value = "прим-Тип2";

        // Give the source instance parameter per-type defaults so the transfer
        // can be proven by value, not only by counters.
        if (!SetInstanceParameterDefaults(FamilySeed.InstanceParam, type1Value, type2Value))
        {
            Skip.Test("The seeded family has no instance parameter to map");
        }

        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.InstanceParam + "\"}," +
            "\"target\":{\"name\":\"" + targetName + "\"},\"createTarget\":true}]}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 1)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(1);
        await Assert.That(previewData.WillChangeCount).IsEqualTo(1);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options(batchSize: 1)
            },
            Doc);

        // The core of B.6: instance-scope targets are not a failure.
        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        await Assert.That(result.State).IsEqualTo(JobState.CompletedUnverified);

        var items = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(1);
        await Assert.That(items.Count(item => item.State == JobItemState.Mismatch)).IsEqualTo(0);

        var totals = JobTotals.FromItems(items);
        await Assert.That(totals.Failed).IsEqualTo(0);
        await Assert.That(totals.Mismatch).IsEqualTo(0);
        await Assert.That(totals.NotVerifiable).IsEqualTo(2);
        await Assert.That(items[0].NotVerifiable).IsEqualTo(2);

        // The values really moved: reopen the family and read the created
        // instance parameter's per-type defaults.
        var transferred = ReadInstanceParameterValues(FamilyPrefix + "1", targetName);
        await Assert.That(transferred.TryGetValue(FamilySeed.Type1, out var firstValue)).IsTrue();
        await Assert.That(firstValue).IsEqualTo(type1Value);
        await Assert.That(transferred.TryGetValue(FamilySeed.Type2, out var secondValue)).IsTrue();
        await Assert.That(secondValue).IsEqualTo(type2Value);

        // Describe contract: the status payload carries notVerified and a hint
        // that says there is nothing to retry.
        var statusTool = new GetJobStatusTool(Store, Runner);
        var context = new AgentToolContext(
            new InlineAwaitableEvent(),
            DocumentId,
            Guid.Empty);
        using var args = JsonDocument.Parse("{\"jobId\":\"" + jobId.ToString("D") + "\"}");
        var status = await statusTool.ExecuteAsync(context, args.RootElement, CancellationToken.None);

        await Assert.That(status.Success).IsTrue();
        var data = status.Data!.Value;
        await Assert.That(data.GetProperty("state").GetString()).IsEqualTo("CompletedUnverified");
        await Assert.That(data.GetProperty("mustVerify").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("notVerified").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("next").GetString()!.Contains("not verifiable")).IsTrue();
        await Assert.That(data.GetProperty("next").GetString()!.Contains("rollback_job")).IsTrue();

        // The new terminal state is still rollbackable.
        var rollback = Runner.Rollback(Doc, jobId, dryRun: false);
        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);

        // Every family document must be closed: only the project stays open.
        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    /// <summary>Sets per-type defaults of an instance parameter through EditFamily.</summary>
    private bool SetInstanceParameterDefaults(string parameterName, string type1Value, string type2Value)
    {
        var family = FindFamilyByName(FamilyPrefix + "1");
        if (family is null)
        {
            return false;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(family);
            var manager = familyDoc.FamilyManager;
            var parameter = FindFamilyParameter(manager, parameterName);
            if (parameter is null)
            {
                return false;
            }

            using (var transaction = new Transaction(familyDoc, "Seed instance defaults"))
            {
                transaction.Start();
                foreach (FamilyType type in manager.Types)
                {
                    string? value;
                    if (string.Equals(type.Name, FamilySeed.Type1, StringComparison.OrdinalIgnoreCase))
                    {
                        value = type1Value;
                    }
                    else if (string.Equals(type.Name, FamilySeed.Type2, StringComparison.OrdinalIgnoreCase))
                    {
                        value = type2Value;
                    }
                    else
                    {
                        continue;
                    }

                    manager.CurrentType = type;
                    manager.Set(parameter, value);
                }

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    return false;
                }
            }

            familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
            {
                familyDoc.Close(false);
            }
        }
    }

    /// <summary>
    /// Reads the per-type defaults of an instance parameter by reopening the
    /// family. Read-only: the family document is closed without loading back.
    /// </summary>
    private Dictionary<string, string?> ReadInstanceParameterValues(string familyName, string parameterName)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var family = FindFamilyByName(familyName);
        if (family is null)
        {
            return values;
        }

        var familyDoc = Doc.EditFamily(family);
        try
        {
            var manager = familyDoc.FamilyManager;
            var parameter = FindFamilyParameter(manager, parameterName);
            if (parameter is null)
            {
                return values;
            }

            foreach (FamilyType type in manager.Types)
            {
                values[type.Name] = type.AsString(parameter);
            }
        }
        finally
        {
            if (familyDoc.IsValidObject)
            {
                familyDoc.Close(false);
            }
        }

        return values;
    }

    private RevitFamily? FindFamilyByName(string familyName)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily family &&
                string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return null;
    }

    private static FamilyParameter? FindFamilyParameter(RevitFamilyManager manager, string parameterName)
    {
        foreach (FamilyParameter parameter in manager.Parameters)
        {
            if (string.Equals(parameter.Definition.Name, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        return null;
    }

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
