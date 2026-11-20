using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Net-zero in-memory poke for doc-to-doc family reloads (see #209 for the full
/// investigation). Revit decides "the family has not changed" from a dirty stamp
/// on the source document, not from content, so LoadFamily from a freshly opened
/// document (a backup .rfa that was never edited) can silently no-op even with
/// the callbacks fired — Autodesk confirmed the API/UI divergence. Adding and
/// removing a scratch parameter in two committed transactions flips the stamp
/// without saving anything: the document on disk stays untouched and the reload
/// actually replaces the definition.
/// </summary>
internal static class AgentFamilyReloadPoke
{
    internal static bool TryPoke(Document sourceDoc, ITransactionService transactions, string familyName)
    {
        try
        {
            var existingNames = new HashSet<string>(
                sourceDoc.FamilyManager.GetParameters().Select(p => p.Definition.Name),
                StringComparer.OrdinalIgnoreCase);
            var pokeName = "__SmartConPoke__";
            for (var i = 1; existingNames.Contains(pokeName); i++)
                pokeName = "__SmartConPoke" + i + "__";

            var added = transactions.RunInTransaction(
                sourceDoc,
                "SmartCon Agent: Reload Poke (add)",
                d => AddScratchParameter(d, pokeName));
            if (!added)
            {
                SmartConLogger.Warn(
                    "Agent poke add-parameter transaction failed for '" + familyName + "' " +
                    "[Action: reload proceeds without the poke — the read-back will show whether it applied]");
                return false;
            }

            var removed = transactions.RunInTransaction(
                sourceDoc,
                "SmartCon Agent: Reload Poke (remove)",
                d => RemoveScratchParameter(d, pokeName));
            if (!removed)
            {
                SmartConLogger.Warn(
                    "Agent poke remove-parameter transaction failed for '" + familyName + "' " +
                    "[Action: reload proceeds without the poke — the read-back will show whether it applied]");
                return false;
            }

            SmartConLogger.Info(
                "Agent poke applied to '" + familyName + "' (net-zero, in-memory only) — " +
                "changedness stamp flipped (#209)");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent poke failed for '" + familyName + "': " + ex.GetType().Name + ": " + ex.Message +
                " [Action: reload proceeds without the poke — the read-back will show whether it applied]");
            return false;
        }
    }

    /// <summary>
    /// Same poke for job actions, which manage family transactions inline (the
    /// job undo path already commits its own <c>Transaction</c> objects there)
    /// and have no <c>ITransactionService</c> in scope.
    /// </summary>
    internal static bool TryPokeLocal(Document sourceDoc, string familyName)
    {
        try
        {
            var pokeName = "__SmartConPoke__";
            using (var add = new Transaction(sourceDoc, "SmartCon Agent: Reload Poke (add)"))
            {
                add.Start();
                AddScratchParameter(sourceDoc, pokeName);
                add.Commit();
            }

            using (var remove = new Transaction(sourceDoc, "SmartCon Agent: Reload Poke (remove)"))
            {
                remove.Start();
                RemoveScratchParameter(sourceDoc, pokeName);
                remove.Commit();
            }

            SmartConLogger.Info(
                "Agent poke applied to '" + familyName + "' (net-zero, in-memory only) — " +
                "changedness stamp flipped (#209)");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent poke failed for '" + familyName + "': " + ex.GetType().Name + ": " + ex.Message +
                " [Action: reload proceeds without the poke — the read-back will show whether it applied]");
            return false;
        }
    }

    private static void AddScratchParameter(Document doc, string pokeName)
    {
#if REVIT2022_OR_GREATER
        doc.FamilyManager.AddParameter(pokeName, GroupTypeId.General, SpecTypeId.String.Text, false);
#else
        doc.FamilyManager.AddParameter(pokeName, BuiltInParameterGroup.PG_GENERAL, ParameterType.Text, false);
#endif
    }

    private static void RemoveScratchParameter(Document doc, string pokeName)
    {
        var scratch = doc.FamilyManager.GetParameters()
            .FirstOrDefault(p => string.Equals(
                p.Definition.Name,
                pokeName,
                StringComparison.Ordinal));
        if (scratch is not null)
            doc.FamilyManager.RemoveParameter(scratch);
        doc.Regenerate();
    }
}
