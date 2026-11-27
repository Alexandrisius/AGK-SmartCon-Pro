using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// One suppressed Revit dialog: the id, the message text and the answer the
/// suppressor gave on the agent's behalf.
/// </summary>
internal readonly record struct AgentSuppressedDialog(string DialogId, string Message, string Answer);

/// <summary>
/// Dismisses modal Revit dialogs that appear while a family document is being
/// opened or loaded (the "family geometry dependencies may change
/// unpredictably" TaskDialog on <c>Document.EditFamily</c>, family upgrade
/// prompts) — the same pattern as <c>ShareProjectCommand</c>: subscribe to
/// <see cref="UIApplication.DialogBoxShowing"/> for the duration of the
/// operation and answer with <see cref="DialogBoxShowingEventArgs.OverrideResult"/>.
/// Every dismissed dialog is recorded so the tool can report it in
/// <c>suppressedDialogs[]</c> instead of hiding it (live feedback v17, P0 1.1:
/// one read-only verification call hung until a human clicked OK).
/// <para>
/// No-op without a UI context (integration tests drive tools through a bare
/// <c>IRevitContext</c>): suppression is best effort, never fatal.
/// </para>
/// </summary>
internal sealed class AgentFamilyDialogSuppressor : IDisposable
{
    private UIApplication? _uiApplication;
    private EventHandler<DialogBoxShowingEventArgs>? _handler;

    public List<AgentSuppressedDialog> Suppressed { get; } = new();

    /// <summary>
    /// Null in UI-less hosts (integration tests): RevitAPIUI must never load
    /// there — a FileLoadException (0x8007045A) killed the whole suite. The
    /// core stays behind a NoInlining guard (#153 pattern).
    /// </summary>
    internal static AgentFamilyDialogSuppressor? BeginSafe(IRevitContext? context) =>
        context is SmartCon.Revit.Context.IRevitUIContext ? Begin(context) : null;

    /// <summary>
    /// Subscribes when the context exposes a UIApplication; otherwise returns
    /// a no-op instance (tests, headless runs).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static AgentFamilyDialogSuppressor Begin(IRevitContext? context)
    {
        var suppressor = new AgentFamilyDialogSuppressor();
        if (context is IRevitUIContext uiContext)
        {
            try
            {
                suppressor._uiApplication = uiContext.GetUIApplication();
            }
            catch (InvalidOperationException)
            {
                // Zero-document or not initialized yet — nothing to subscribe to.
            }
        }

        if (suppressor._uiApplication is null)
            return suppressor;

        suppressor._handler = suppressor.OnDialogBoxShowing;
        suppressor._uiApplication.DialogBoxShowing += suppressor._handler;
        return suppressor;
    }

    private void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs args)
    {
        var message = (args as TaskDialogShowingEventArgs)?.Message ?? string.Empty;
        var dialogId = args.DialogId ?? string.Empty;
        string answer;
        try
        {
            // 1 = IDOK: confirms "OK"-style family warnings (dependency
            // behaviour, upgrade prompts). Custom-button dialogs fall back to
            // the left-most command link semantics per the API docs.
            args.OverrideResult(1);
            answer = "ok";
        }
        catch (Exception)
        {
            // The dialog refused the override — it stays visible; record it
            // honestly so the payload says the call may hang on a click.
            answer = "not-dismissed";
        }

        if (Suppressed.Count < 50)
        {
            Suppressed.Add(new AgentSuppressedDialog(dialogId, message, answer));
        }
    }

    public void Dispose()
    {
        if (_uiApplication is not null && _handler is not null)
        {
            try
            {
                _uiApplication.DialogBoxShowing -= _handler;
            }
            catch (Exception)
            {
                // Revit is shutting down — the subscription dies with it.
            }
        }

        _uiApplication = null;
        _handler = null;
    }
}
