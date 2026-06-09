using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Implementation of <see cref="IActiveDocumentClassifier"/>. Asks
/// <see cref="IFamilyManagerAwaitableEvent"/> for the active document
/// and classifies it as <see cref="ActiveDocumentKind.Family"/>,
/// <see cref="ActiveDocumentKind.Project"/> or
/// <see cref="ActiveDocumentKind.None"/>. The classification runs
/// inside an <see cref="IFamilyManagerAwaitableEvent.RaiseAsync{T}(Func{object, T}, CancellationToken)"/>
/// callback to be on the Revit UI thread (I-01).
/// </summary>
internal sealed class ActiveDocumentClassifier : IActiveDocumentClassifier
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;

    public ActiveDocumentClassifier(IFamilyManagerAwaitableEvent awaitableEvent)
    {
        _awaitableEvent = awaitableEvent
            ?? throw new ArgumentNullException(nameof(awaitableEvent));
    }

    public Task<ActiveDocumentKind> ClassifyAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("ActiveClassifier",
            ("Method", "ClassifyAsync"));
        return _awaitableEvent.RaiseAsync<ActiveDocumentKind>(obj =>
        {
            using var _uiScope = SmartConLogger.BeginScope("ActiveClassifier",
                ("Method", "ClassifyAsync"),
                ("Thread", "RevitUI"));
            try
            {
                var uiApp = (UIApplication)obj;
                var activeDoc = uiApp.ActiveUIDocument?.Document;
                if (activeDoc is null)
                {
                    SmartConLogger.Debug("No active document → None");
                    return ActiveDocumentKind.None;
                }

                var kind = activeDoc.IsFamilyDocument
                    ? ActiveDocumentKind.Family
                    : ActiveDocumentKind.Project;

                SmartConLogger.Info(
                    $"Active doc: title='{activeDoc.Title}', " +
                    $"isFamily={activeDoc.IsFamilyDocument} → {kind}");
                return kind;
            }
            catch (Exception ex)
            {
                // Defensive: any cast failure or Revit API exception is
                // coerced to None so the import command can show a
                // clean error dialog rather than crashing the UI thread.
                SmartConLogger.Error(
                    $"Classification failed: {ex.Message}");
                return ActiveDocumentKind.None;
            }
        }, ct);
    }
}
