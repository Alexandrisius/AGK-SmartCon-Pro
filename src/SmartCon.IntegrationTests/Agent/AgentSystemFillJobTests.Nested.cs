using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Real integration of the set_parameters product path ExpandWithSubComponents:
/// hosts are seeded with the case-4 benchmark mechanics
/// (<see cref="SmartCon.IntegrationTests.Agent.Support.NestedSharedFamilySeed"/>),
/// the job selects by elementIds of BOTH hosts with includeSubComponents=true,
/// and every nested shared instance must receive the parameter value. The
/// without-flag control (subcomponents stay out of the selection) lives in the
/// single-host test of the main partial. Partial of
/// <see cref="AgentSystemFillJobTests"/>.
/// </summary>
public sealed partial class AgentSystemFillJobTests
{
    [Test]
    public async Task IncludeSubComponents_TwoHosts_BothNestedSharedInstancesFilled()
    {
        if (!_nestedSeeded || _hostSymbolIdValue == 0)
            Skip.Test(_nestedSkipReason ?? "Вложенное shared-семейство не собралось");
        if (!_systemSeeded)
            Skip.Test(_systemSkipReason ?? "Систему SC_Т1 не удалось наполнить");

        // Второй хост — сценарий кейса 4 (Н1/Н2): два хозяина одним вызовом джобы.
        long host2InstanceId = 0;
        _transactions!.RunInTransaction(Doc, "Seed second host", doc =>
        {
            if (doc.GetElement(ElementIdCompat.Create(_hostSymbolIdValue)) is not FamilySymbol symbol)
                return;
            symbol.Activate();
            var host = doc.Create.NewFamilyInstance(
                new XYZ(50, 10, 0), symbol, StructuralType.NonStructural);
            host2InstanceId = host.Id.GetValue();
            // Субкомпоненты материализуются только при регенерации (C4-04).
            doc.Regenerate();
        });
        await Assert.That(host2InstanceId).IsGreaterThan(0);

        var nested2Id = (Doc.GetElement(ElementIdCompat.Create(host2InstanceId)) as FamilyInstance)
            ?.GetSubComponentIds().FirstOrDefault()?.GetValue() ?? 0;
        await Assert.That(nested2Id).IsGreaterThan(0);

        var (job, previewData) = await PreviewAndApply(
            "{\"select\":{\"elementIds\":[" + _hostInstanceId + "," + host2InstanceId +
            "],\"includeSubComponents\":true},\"parameter\":\"" + ParamName +
            "\",\"value\":\"Т1\"}");
        await Assert.That(previewData.Count).IsEqualTo(4);

        var items = await Store.LoadAllItemsAsync(job.JobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(4);

        // Сердце кейса 4: значение у хостов И у их вложенных shared-фланцев,
        // каждый фланец остаётся субкомпонентом своего хозяина.
        foreach (var (hostId, nestedId) in new[] { (_hostInstanceId, _nestedInstanceId), (host2InstanceId, nested2Id) })
        {
            await Assert.That(Doc.GetElement(ElementIdCompat.Create(hostId))?
                .LookupParameter(ParamName)?.AsString()).IsEqualTo("Т1");
            var nested = Doc.GetElement(ElementIdCompat.Create(nestedId)) as FamilyInstance;
            await Assert.That(nested?.LookupParameter(ParamName)?.AsString()).IsEqualTo("Т1");
            await Assert.That(nested?.SuperComponent?.Id.GetValue()).IsEqualTo(hostId);
        }
    }
}
