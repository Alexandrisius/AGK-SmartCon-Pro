using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;

namespace SmartCon.App.Services;

/// <summary>
/// Routes "show About" requests from feature modules to the existing
/// About dialog (update channel, changelog, update check) — ADR-058 (#173).
/// Lives in App because <see cref="IAboutViewModelFactory"/> is a
/// PipeConnect service and the dependency rule keeps feature modules
/// (FamilyManager) away from it.
/// </summary>
internal sealed class AboutDialogService : IAboutDialogService
{
    private readonly IAboutViewModelFactory _factory;
    private readonly IDialogPresenter _presenter;

    public AboutDialogService(IAboutViewModelFactory factory, IDialogPresenter presenter)
    {
        _factory = factory;
        _presenter = presenter;
    }

    public void ShowAbout() => _presenter.ShowDialog(_factory.Create());
}
