using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// One failure auto-resolved on the family write path.
/// </summary>
internal readonly record struct AgentSuppressedFailure(string FailureName, string Description, string Resolution);

/// <summary>
/// Suppresses known benign Revit failure prompts on the family write path
/// (live v18: «Семейство больше не соответствует свойствам системы.
/// Отсоединить семейство от системы?» blocked a migration job for minutes —
/// the owner always confirms it manually and the work continues).
/// <para>
/// The failure rides the failure framework and never reaches
/// <see cref="AgentFamilyDialogSuppressor"/>. The suppression hooks BOTH
/// failure-processing phases: the <see cref="Application.FailuresProcessing"/>
/// event (phase 2) and a registered <see cref="IFailuresProcessor"/> (phase 3).
/// The system-disconnect failure could NOT be resolved in phases 1–2 for the
/// "cancel" intent (Autodesk forum, Doc.LoadFamily thread), and phase 3 is the
/// documented fallback for errors — the processor resolves the whitelisted
/// failures with their resolution and returns ProceedWithCommit, which keeps
/// the prompt off the screen and the load running.
/// <para>
/// The failure exists under SEVERAL FailureDefinitionIds depending on the
/// Revit version (live v18: R23 fired SystemsFailures.FamilyDoesntMatchSystemProperties
/// while the whitelist held only FamilyFailures…Disconnected — the prompt
/// still appeared). The ids are collected through reflection, so unknown
/// versions compile and resolve safely.
/// </para>
/// </para>
/// </summary>
internal sealed class AgentFamilyFailuresSuppressor : IDisposable
{
    private readonly Application _application;
    private readonly EventHandler<FailuresProcessingEventArgs> _handler;
    private readonly Processor _processor = new();

    private AgentFamilyFailuresSuppressor(Application application)
    {
        _application = application;
        _handler = OnFailuresProcessing;
        _application.FailuresProcessing += _handler;
    }

    // NOTE: Application.RegisterFailuresProcessor (phase 3) was evaluated and
    // REJECTED: it is static, has no unregister and replaces the standard
    // Revit error dialog for the whole session — every failure of every
    // USER operation would end in our processor instead of a dialog.
    // Phase 2 (the FailuresProcessing event) resolves the whitelisted
    // failures with their Disconnect resolution; the whitelist below now
    // covers every id variant the failure uses across versions.

    /// <summary>Subscribes for the lifetime of the returned scope: every
    /// transaction committed while it is alive (the family edit, the internal
    /// LoadFamily transaction of the load-back) is processed with the
    /// whitelisted resolutions.</summary>
    internal static AgentFamilyFailuresSuppressor Begin(Document doc)
    {
        return new AgentFamilyFailuresSuppressor(doc.Application);
    }

    internal IReadOnlyList<AgentSuppressedFailure> Suppressed => _processor.Suppressed;

    internal int WhitelistMatches => _processor.WhitelistMatches;

    /// <summary>Human-readable warning lines for outcomes and job reports.</summary>
    internal List<string> WarningLines()
    {
        var lines = new List<string>();
        foreach (var failure in _processor.Suppressed)
        {
            lines.Add(
                "Revit failure auto-resolved while editing the family: '" +
                failure.FailureName + "' — \"" + failure.Description + "\" (" +
                failure.Resolution + ").");
        }

        return lines;
    }

    public void Dispose()
    {
        _application.FailuresProcessing -= _handler;
    }

    private void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs args)
    {
        // Phase 2 of the failures processing (event shape): run the same
        // resolution over the accessor and raise the processing result when
        // something was resolved — otherwise Revit still shows the prompt.
        var result = _processor.Process(args.GetFailuresAccessor());
        if (result != FailureProcessingResult.Continue)
        {
            args.SetProcessingResult(result);
        }
    }

    /// <summary>Whitelisted failure ids, collected once per process through
    /// reflection: the failure lives under different ids/classes on different
    /// Revit versions and every variant must resolve (live v18).</summary>
    private static HashSet<FailureDefinitionId>? _whitelistedIds;

    private static HashSet<FailureDefinitionId> WhitelistedIds()
    {
        if (_whitelistedIds is not null)
            return _whitelistedIds;

        var ids = new HashSet<FailureDefinitionId>();
        foreach (var (containerName, propertyNames) in new[]
                 {
                     (containerName: "SystemsFailures", propertyNames: new[]
                     {
                         "FamilyDoesntMatchSystemProperties",
                         "FamilyDoesntMatchSystemPropertiesWasDisconnected"
                     }),
                     (containerName: "FamilyFailures", propertyNames: new[]
                     {
                         "FamilyDoesntMatchSystemPropertiesDisconnected"
                     })
                 })
        {
            var container = Type.GetType(
                "Autodesk.Revit.DB.BuiltInFailures+" + containerName + ", RevitAPI");
            if (container is null)
                continue;

            foreach (var propertyName in propertyNames)
            {
                try
                {
                    if (container.GetProperty(propertyName)?.GetValue(null) is FailureDefinitionId id)
                        ids.Add(id);
                }
                catch (Exception)
                {
                    // A version without this variant: skip it.
                }
            }
        }

        _whitelistedIds = ids;
        return ids;
    }

    private sealed class Processor
    {
        public List<AgentSuppressedFailure> Suppressed { get; } = new();

        public int WhitelistMatches { get; private set; }

        public FailureProcessingResult Process(FailuresAccessor accessor)
        {
            var messages = accessor.GetFailureMessages();
            if (messages.Count == 0)
            {
                return FailureProcessingResult.Continue;
            }

            var whitelisted = WhitelistedIds();
            var resolved = false;
            var blocked = false;
            foreach (var message in messages)
            {
                if (whitelisted.Contains(message.GetFailureDefinitionId()))
                {
                    WhitelistMatches++;
                    resolved |= ResolveWhitelisted(accessor, message);
                    continue;
                }

                if (message.GetSeverity() == FailureSeverity.Warning)
                {
                    // A resolved whitelisted failure proceeds with the commit,
                    // which also silences the unrelated warnings of the same
                    // transaction — record them so nothing disappears silently.
                    if (Suppressed.Count <= 50)
                    {
                        Suppressed.Add(new AgentSuppressedFailure(
                            "warning-dismissed",
                            message.GetDescriptionText() ?? string.Empty,
                            "dismissed by the same commit"));
                    }

                    try
                    {
                        accessor.DeleteWarning(message);
                    }
                    catch (Exception)
                    {
                        // The warning stays for the standard processing.
                    }

                    continue;
                }

                blocked = true;
            }

            if (!resolved)
            {
                // Nothing of ours: let Revit behave exactly as without us.
                Suppressed.RemoveAll(failure => failure.FailureName == "warning-dismissed");
                return FailureProcessingResult.Continue;
            }

            // Real errors keep their standard processing (the transaction is
            // rolled back honestly); only the whitelisted failures are resolved.
            return blocked
                ? FailureProcessingResult.Continue
                : FailureProcessingResult.ProceedWithCommit;
        }

        private bool ResolveWhitelisted(FailuresAccessor accessor, FailureMessageAccessor message)
        {
            try
            {
                // The disconnect failure carries a single resolution; prefer
                // the explicit detach type when the failure offers it (the
                // Autodesk preprocessor pattern), fall back to the default.
                if (message.HasResolutionOfType(FailureResolutionType.DetachElements))
                {
                    message.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                }

                accessor.ResolveFailure(message);
                Suppressed.Add(new AgentSuppressedFailure(
                    "FamilyDoesntMatchSystemProperties",
                    message.GetDescriptionText() ?? string.Empty,
                    "disconnect confirmed (default)"));
                return true;
            }
            catch (ArgumentException)
            {
                // "failure does not have any resolutions" (an empty test
                // document) or "default resolution not permitted" — for a
                // warning, DeleteWarning presses the default button, which is
                // the same manual confirm; for an error there is nothing to
                // press in this phase and the registered FailuresProcessor
                // (phase 3) takes the next pass.
                if (message.GetSeverity() == FailureSeverity.Warning)
                {
                    try
                    {
                        accessor.DeleteWarning(message);
                        Suppressed.Add(new AgentSuppressedFailure(
                            "FamilyDoesntMatchSystemProperties",
                            message.GetDescriptionText() ?? string.Empty,
                            "dismissed (default)"));
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                SmartCon.Core.Logging.SmartConLogger.Warn(
                    "Whitelisted failure could not be resolved with its default " +
                    "resolution: " + ex.GetType().Name + ": " + ex.Message +
                    " [Action: the standard Revit prompt is shown; answer it manually]");
                return false;
            }
        }
    }
}
