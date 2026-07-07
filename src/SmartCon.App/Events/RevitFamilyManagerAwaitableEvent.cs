using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;

namespace SmartCon.App.Events;

/// <summary>
/// <see cref="IExternalEventHandler"/> adapter for
/// <see cref="IFamilyManagerAwaitableEvent"/>. Bridges Revit's
/// <c>ExternalEvent</c> message-loop contract to the pure-C#
/// <see cref="FamilyManagerAwaitableEvent"/> queue.
///
/// <para>Why this lives in <c>SmartCon.App</c> and not
/// <c>SmartCon.FamilyManager</c>:</para>
/// <list type="bullet">
///   <item><description><see cref="IExternalEventHandler"/> lives in
///     the <c>RevitAPIUI</c> assembly, which is a runtime-only
///     dependency available exclusively inside the Revit host.</description></item>
///   <item><description>Implementing it in
///     <see cref="FamilyManagerAwaitableEvent"/> would force
///     <c>SmartCon.FamilyManager</c> to load <c>RevitAPIUI</c> at
///     type-init time, which breaks unit tests on CI.</description></item>
///   <item><description>By keeping the adapter in
///     <c>SmartCon.App</c> (where the Revit host is always present)
///     the awaitable queue itself stays a pure, mockable
///     abstraction.</description></item>
/// </list>
/// </summary>
internal sealed class RevitFamilyManagerAwaitableEvent : IExternalEventHandler
{
    private readonly IFamilyManagerAwaitableEvent _awaitable;

    public RevitFamilyManagerAwaitableEvent(IFamilyManagerAwaitableEvent awaitable)
    {
        _awaitable = awaitable
            ?? throw new ArgumentNullException(nameof(awaitable));
    }

    /// <summary>
    /// Called by Revit on the UI thread. Forwards the
    /// <c>UIApplication</c> to the awaitable queue, which drains
    /// every pending <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>.
    /// </summary>
    public void Execute(UIApplication app) => _awaitable.ProcessQueue(app);

    /// <summary>
    /// Name shown in Revit's External Event list.
    /// </summary>
    public string GetName() => "SmartCon.FamilyManager.AwaitableEvent";
}
