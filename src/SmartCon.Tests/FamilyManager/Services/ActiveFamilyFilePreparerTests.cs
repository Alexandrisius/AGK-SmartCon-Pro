using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Smoke tests for <see cref="ActiveFamilyFilePreparer"/>. The Revit
/// API cannot be exercised in unit tests (UIApplication / UIDocument
/// are sealed types with no public constructor and the cast to
/// <c>UIApplication</c> requires the RevitAPIUI assembly to be
/// loadable), so we only cover the external-event error path here.
/// Positive paths (family detected, sidecar copied) are validated
/// by manual testing inside Revit and by the sidecar locator tests
/// in <see cref="LocalFamilySidecarLocatorTests"/>.
/// </summary>
public sealed class ActiveFamilyFilePreparerTests
{
    [Fact]
    public async Task Prepare_NotInitialized_Throws()
    {
        var fake = new UninitializedAwaitableEvent();
        var locator = new LocalFamilySidecarLocator();
        var sut = new ActiveFamilyFilePreparer(fake, locator);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.PrepareActiveFamilyAsync());
    }

    private sealed class UninitializedAwaitableEvent : IFamilyManagerAwaitableEvent
    {
        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
            => throw new InvalidOperationException("revit boom — not initialized");

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
            => throw new InvalidOperationException("revit boom — not initialized");

        public Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default)
            => throw new InvalidOperationException("revit boom — not initialized");

        public void ProcessQueue(object revitApp)
            => throw new InvalidOperationException("revit boom — not initialized");

        public void Initialize(Action onRaise)
            => throw new InvalidOperationException("revit boom — not initialized");
    }
}
