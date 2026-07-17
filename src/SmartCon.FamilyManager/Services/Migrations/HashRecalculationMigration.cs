using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services.Migrations;

/// <summary>
/// <see cref="IDatabaseMigration"/> adapter for the Issue #126 hash
/// recalculation (v1 → v2 rename-invariant). Owns the progress-dialog flow
/// (<see cref="HashRecalculationProgressViewModel"/>); the heavy lifting
/// stays in <see cref="ICatalogHashRecalculationService"/>.
/// </summary>
public sealed class HashRecalculationMigration : IDatabaseMigration
{
    private readonly ICatalogHashRecalculationService _recalculationService;
    private readonly IFamilyManagerDialogService _dialogService;

    public HashRecalculationMigration(
        ICatalogHashRecalculationService recalculationService,
        IFamilyManagerDialogService dialogService)
    {
        _recalculationService = recalculationService ?? throw new ArgumentNullException(nameof(recalculationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public string Id => "hash-v2";

    public int Order => 10;

    public Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
        => _recalculationService.CountPendingAsync(revitMajorVersion, ct);

    public async Task RunAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        var pending = await _recalculationService.CountPendingAsync(revitMajorVersion, ct).ConfigureAwait(true);
        if (pending <= 0) return;

        var vm = new HashRecalculationProgressViewModel(
            _recalculationService,
            _dialogService,
            revitMajorVersion,
            pending);
        _dialogService.ShowHashRecalculationProgressDialog(vm);

        try
        {
            await vm.RunAsync().ConfigureAwait(true);
            await vm.DialogCompletion.ConfigureAwait(true);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
