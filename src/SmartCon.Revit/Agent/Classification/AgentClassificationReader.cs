using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Revit.Agent.Classification;

/// <summary>
/// Reads the classification table the document really carries: the Assembly Code
/// table behind the «Код по классификатору» picker — the only classification
/// structure the public API exposes for a document. No transaction: the table is
/// read as-is, an empty table stays empty (it is not loaded from anywhere here).
/// </summary>
internal static class AgentClassificationReader
{
    internal static AgentClassificationTable Read(Document doc)
    {
        var table = AssemblyCodeTable.GetAssemblyCodeTable(doc);

        string? source;
        using (var reference = table.GetExternalResourceReference(
            ExternalResourceTypes.BuiltInExternalResourceTypes.AssemblyCodeTable))
        {
            source = reference.InSessionPath;
        }

        var rows = new List<AgentClassificationEntry>();
        using var entries = table.GetKeyBasedTreeEntries();
        if (entries is ClassificationEntries classification)
        {
            foreach (ClassificationEntry entry in classification)
            {
                var categoryId = entry.CategoryId;
                rows.Add(new AgentClassificationEntry
                {
                    Code = entry.Key,
                    Description = entry.Description ?? string.Empty,
                    Level = entry.Level,
                    ParentCode = entry.ParentKey,
                    CategoryId = categoryId is not null && categoryId != ElementId.InvalidElementId
                        ? categoryId.GetValue()
                        : null
                });
            }
        }

        return AgentClassificationTable.Build(rows, source);
    }
}
