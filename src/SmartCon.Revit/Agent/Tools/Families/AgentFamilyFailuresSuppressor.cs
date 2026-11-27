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
/// (live feedback v18: «Семейство больше не соответствует свойствам системы.
/// Отсоединить семейство от системы?» blocked a migration job for minutes —
/// the owner always confirms it manually and the work continues). Failures
/// ride their own framework and never reach <see cref="AgentFamilyDialogSuppressor"/>;
/// the hook is the <see cref="Application.FailuresProcessing"/> event with a
/// preprocessor that resolves the whitelisted failures with their default
/// resolution and reports every dismissal. Pure DB types: safe in UI-less
/// integration-test hosts. Anything not whitelisted — and every error-severity
/// failure — is left to the standard Revit UI.
/// </summary>
internal sealed class AgentFamilyFailuresSuppressor : IDisposable
{
    private readonly Application _application;
    private readonly EventHandler<FailuresProcessingEventArgs> _handler;
    private readonly Preprocessor _preprocessor = new();

    private AgentFamilyFailuresSuppressor(Application application)
    {
        _application = application;
        _handler = OnFailuresProcessing;
        _application.FailuresProcessing += _handler;
    }

    /// <summary>Subscribes for the lifetime of the returned scope: every
    /// transaction committed while it is alive (the family edit, the internal
    /// LoadFamily transaction of the load-back) gets the preprocessor.</summary>
    internal static AgentFamilyFailuresSuppressor Begin(Document doc) =>
        new(doc.Application);

    internal IReadOnlyList<AgentSuppressedFailure> Suppressed => _preprocessor.Suppressed;

    /// <summary>Whitelist matches seen — proves the event path works even when
    /// the posted failure carries no resolutions in an empty context (IT).</summary>
    internal int WhitelistMatches => _preprocessor.WhitelistMatches;

    /// <summary>Human-readable warning lines for outcomes and job reports.</summary>
    internal List<string> WarningLines()
    {
        var lines = new List<string>();
        foreach (var failure in _preprocessor.Suppressed)
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
        // Event-shape of the failures API (there is no SetFailuresPreprocessor
        // here): run the same preprocessor over the accessor and, when it
        // resolved something, raise the processing result so Revit does not
        // show the dismissed prompts (Autodesk docs, FailuresProcessingEventArgs).
        var result = _preprocessor.PreprocessFailures(args.GetFailuresAccessor());
        if (result != FailureProcessingResult.Continue)
        {
            args.SetProcessingResult(result);
        }
    }

    private sealed class Preprocessor : IFailuresPreprocessor
    {
        public List<AgentSuppressedFailure> Suppressed { get; } = new();

        /// <summary>Whitelist matches seen (with or without an applicable resolution).</summary>
        public int WhitelistMatches { get; set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var messages = accessor.GetFailureMessages();
            if (messages.Count == 0)
            {
                return FailureProcessingResult.Continue;
            }

            var resolved = false;
            var blocked = false;
            foreach (var message in messages)
            {
                if (IsWhitelisted(message))
                {
                    // The default resolution mirrors the owner's manual answer
                    // (confirm the disconnect); ProceedWithCommit is what keeps
                    // the prompt off the screen (Autodesk developer blog:
                    // ResolveFailure + Continue still shows the dialog).
                    try
                    {
                        accessor.ResolveFailure(message);
                        resolved = true;
                        Suppressed.Add(new AgentSuppressedFailure(
                            "FamilyDoesntMatchSystemPropertiesDisconnected",
                            message.GetDescriptionText() ?? string.Empty,
                            "disconnect confirmed (default)"));
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException)
                    {
                        // "failure does not have any resolutions" — the failure
                        // carries its buttons only in a real family/system
                        // context. For a warning, DeleteWarning presses the
                        // default button, which is the same manual confirm
                        // (Autodesk developer blog: it "only uses the default
                        // option" — exactly the owner's practice).
                        WhitelistMatches++;
                        var severity = message.GetSeverity();
                        if (severity == FailureSeverity.Warning)
                        {
                            accessor.DeleteWarning(message);
                            resolved = true;
                            Suppressed.Add(new AgentSuppressedFailure(
                                "FamilyDoesntMatchSystemPropertiesDisconnected",
                                message.GetDescriptionText() ?? string.Empty,
                                "dismissed (default)"));
                        }
                        else
                        {
                            SmartCon.Core.Logging.SmartConLogger.Warn(
                                "Whitelisted failure has no resolutions in this context and " +
                                "severity is " + severity +
                                " [Action: the standard Revit prompt is shown; answer it manually]");
                        }
                    }
                    catch (Exception ex)
                    {
                        SmartCon.Core.Logging.SmartConLogger.Warn(
                            "Whitelisted failure could not be resolved with its default " +
                            "resolution: " + ex.GetType().Name + ": " + ex.Message +
                            " [Action: the standard Revit prompt is shown; answer it manually]");
                    }

                    continue;
                }

                if (message.GetSeverity() == FailureSeverity.Warning)
                {
                    // A whitelisted resolution proceeds with the commit, which
                    // also silences the unrelated warnings of the same
                    // transaction — record them so nothing disappears silently.
                    if (Suppressed.Count > 50)
                    {
                        continue;
                    }

                    Suppressed.Add(new AgentSuppressedFailure(
                        "warning-dismissed",
                        message.GetDescriptionText() ?? string.Empty,
                        "dismissed by the same commit"));
                    continue;
                }

                blocked = true;
            }

            if (!resolved)
            {
                // Nothing of ours: undo the bookkeeping and let Revit behave
                // exactly as without us.
                Suppressed.RemoveAll(failure => failure.FailureName == "warning-dismissed");
                return FailureProcessingResult.Continue;
            }

            // Real errors keep their standard UI; only the whitelisted
            // warnings are resolved.
            return blocked
                ? FailureProcessingResult.Continue
                : FailureProcessingResult.ProceedWithCommit;
        }

        private static bool IsWhitelisted(FailureMessageAccessor message) =>
            message.GetFailureDefinitionId()
                == BuiltInFailures.FamilyFailures.FamilyDoesntMatchSystemPropertiesDisconnected;
    }
}
