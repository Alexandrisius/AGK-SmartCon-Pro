using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.ViewModels.Cloud;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Cloud Catalog (срез v1, §7.3): мастер «Облачная база», «Опубликовать
/// изменения» (pull-before-push, §7.3.12), «Обновить» = только pull
/// (§7.3.3), бейдж «доступны обновления» и значки-облачка. Облачные вызовы —
/// чистые сервисы без Revit API; долгие операции — modeless ADR-048,
/// шаг аккаунта — модальный диалог.
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    [ObservableProperty] private bool _hasCloudUpdates;
    [ObservableProperty] private bool _isCloudBusy;

    public bool IsSelectedCloudPublished =>
        SelectedConnection?.Connection.CloudLink?.Role == CloudLinkRole.Published;

    public bool IsSelectedCloudSubscribed =>
        SelectedConnection?.Connection.CloudLink?.Role == CloudLinkRole.Subscribed;

    /// <summary>Видимость единой команды «Обновить»: актуализация ИЛИ облачный pull (§7.3.3).</summary>
    public bool ShowUpdateDatabaseCommand => HasAnyDatabaseUpdate || HasCloudUpdates;

    private void NotifyCloudSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSelectedCloudPublished));
        OnPropertyChanged(nameof(IsSelectedCloudSubscribed));
        OnPropertyChanged(nameof(ShowUpdateDatabaseCommand));
        PublishCloudChangesCommand.NotifyCanExecuteChanged();
        CopyCloudInviteCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasCloudUpdatesChanged(bool value) => OnPropertyChanged(nameof(ShowUpdateDatabaseCommand));

    // ── Мастер «Облачная база…» (§7.3.1) ────────────────────────────────

    [RelayCommand]
    private async Task OpenCloudDatabaseWizardAsync(CancellationToken ct)
    {
        var wizard = new CloudDatabaseWizardViewModel();
        if (_dialogService.ShowCloudDatabaseWizard(wizard) != true || !wizard.Accepted)
            return;

        using var _scope = SmartConLogger.BeginScope("CloudUI",
            ("Method", nameof(OpenCloudDatabaseWizardAsync)),
            ("Mode", wizard.Mode.ToString()));
        try
        {
            if (wizard.Mode == CloudDatabaseWizardViewModel.WizardMode.CreateEmpty)
                await CreateEmptyCloudDatabaseAsync(wizard.DatabaseName.Trim(), ct).ConfigureAwait(true);
            else
                await SubscribeByInviteAsync(wizard.Invite!, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Cloud wizard failed: {ex.GetType().Name}: {ex.Message} [Action: проверьте адрес сервера и подключение к сети]");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_OperationFailedTitle) ?? "Облачная операция",
                ex.Message);
        }
    }

    /// <summary>§7.3.1 режим б: серверный каталог + CloudLink, БЕЗ публикации контента.</summary>
    private async Task CreateEmptyCloudDatabaseAsync(string name, CancellationToken ct)
    {
        if (!await EnsureLoggedInAsync(null, ct).ConfigureAwait(true)) return;

        var account = _cloudApi.CurrentAccount!;
        var catalog = await _cloudApi.CreateCatalogAsync(name, ct).ConfigureAwait(true);

        var folder = _dialogService.ShowFolderBrowserDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbSelectPath) ?? "Выберите папку");
        if (folder is null) return;

        var connection = await _databaseManager.CreateDatabaseAsync(name, folder, ct).ConfigureAwait(true);
        await _databaseManager.SetCloudLinkAsync(connection.Id,
            new CloudLink(CloudLinkRole.Published, account.Endpoint, catalog.Id, catalog.Slug, 0), ct).ConfigureAwait(true);

        await ActivateCloudConnectionAsync(connection.Id).ConfigureAwait(true);
        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_WizardCreated)
                ?? "Облачная база «{0}» создана (каталог {1}).",
            name, catalog.Slug);
        SmartConLogger.Info($"Empty cloud database created: '{name}' → catalog '{catalog.Slug}' ({catalog.Id})");
    }

    /// <summary>§7.3.1 «Подключить существующую»: приглашение → subscribe → первый pull → регистрация копии.</summary>
    private async Task SubscribeByInviteAsync(CloudInvite.InviteData invite, CancellationToken ct)
    {
        if (!await EnsureLoggedInAsync(invite.Endpoint, ct).ConfigureAwait(true)) return;

        await _cloudApi.SubscribeAsync(invite.Slug, ct).ConfigureAwait(true);

        // Человеческое имя каталога (slug — только URL-id): «Облачная база», а не oblachnaya-baza.
        var catalog = await _cloudApi.GetCatalogAsync(invite.Slug, ct).ConfigureAwait(true);
        var displayName = string.IsNullOrWhiteSpace(catalog.Name) ? invite.Slug : catalog.Name;

        var targetRoot = CloudPaths.SubscriptionRoot(invite.Slug);
        var (status, failed) = await RunCloudOperationAsync(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PullTitle) ?? "Обновление из облака",
            (progress, token) => _cloudSync.SyncAsync(
                new CloudSyncRequest(invite.Slug, targetRoot, displayName), progress, token),
            result => string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_WizardSubscribed)
                    ?? "Подключено к каталогу «{0}»: получено {1} семейств.",
                invite.Slug, result.ItemsCount),
            ct).ConfigureAwait(true);
        // Валидатор Ф5-6 P2: отмена (FinalStatus == null) — тихий выход,
        // без ConnectDatabaseAsync к недособранной копии.
        if (failed || status is null) return;

        // Копия собрана (или уже была актуальна) — регистрируем и подключаем связь.
        var connection = _databaseManager.ListConnections()
            .FirstOrDefault(c => string.Equals(c.Path, targetRoot, StringComparison.OrdinalIgnoreCase));
        if (connection is null)
            connection = await _databaseManager.ConnectDatabaseAsync(targetRoot, ct).ConfigureAwait(true);

        var link = connection.CloudLink ?? new CloudLink(
            CloudLinkRole.Subscribed, _cloudApi.CurrentAccount!.Endpoint, invite.Slug, invite.Slug, null);
        var remoteSeq = await _cloudSync.GetRemotePublishSeqAsync(invite.Slug, ct).ConfigureAwait(true);
        var updatedConnection = await _databaseManager.SetCloudLinkAsync(connection.Id,
            link with { LastSyncedPublishSeq = remoteSeq }, ct).ConfigureAwait(true);

        await ActivateCloudConnectionAsync(updatedConnection?.Id ?? connection.Id).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(status)) StatusMessage = status!;
    }

    /// <summary>После wizard-флоу: активировать базу и перезагрузить панель.</summary>
    private async Task ActivateCloudConnectionAsync(string connectionId)
    {
        RecomputeActiveBaseMatch();
        RefreshConnections();
        SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == connectionId);
        await RefreshAccessAndLoadTreeAsync().ConfigureAwait(true);
        await RefreshDatabaseUpdateStateAsync().ConfigureAwait(true);
        HasCloudUpdates = false;
        NotifyCloudSelectionChanged();
    }

    // ── «Опубликовать изменения» (§7.3.12) ──────────────────────────────

    private bool CanPublishCloudChanges => IsSelectedCloudPublished && !IsCloudBusy;

    [RelayCommand(CanExecute = nameof(CanPublishCloudChanges))]
    private async Task PublishCloudChangesAsync(CancellationToken ct)
    {
        var connection = SelectedConnection?.Connection;
        if (connection?.CloudLink is not { } link) return;

        IsCloudBusy = true;
        PublishCloudChangesCommand.NotifyCanExecuteChanged();
        try
        {
            if (!await EnsureLoggedInAsync(link.Endpoint, ct).ConfigureAwait(true)) return;

            // Import Validation Gate: карантин «Без категории» не публикуется
            // (валидация привязана к категориям) — автор должен знать это ДО
            // публикации, а не из пустого дерева подписчика.
            var quarantined = await _catalogProvider.SearchAsync(
                new FamilyCatalogQuery(SearchText: null, CategoryFilter: null, StatusFilter: null, Tags: null, Sort: FamilyCatalogSort.NameAsc, Offset: 0, Limit: int.MaxValue, IncludeUncategorized: true), ct).ConfigureAwait(true);
            if (quarantined.Count > 0)
            {
                var names = string.Join(Environment.NewLine, quarantined.Take(5).Select(q => $"• {q.Name}"));
                if (quarantined.Count > 5)
                    names += Environment.NewLine + "…";
                var totalRest = await _catalogProvider.GetItemCountAsync(ct).ConfigureAwait(true) - quarantined.Count;
                var confirmed = _dialogService.ShowConfirmation(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PublishQuarantinedTitle)
                        ?? "Семейства без категории",
                    string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PublishQuarantinedBody)
                            ?? "{0}\r\nОпубликовать остальные ({1})?",
                        names, Math.Max(0, totalRest)));
                if (!confirmed) return;
            }

            var publishedSeq = 0L;
            var (_, failed) = await RunCloudOperationAsync(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PublishTitle) ?? "Публикация в облако",
                async (progress, token) =>
                {
                    var result = await _cloudPublish.PublishAsync(
                        new CloudPublishRequest(link.Slug, link.CatalogId), progress, token).ConfigureAwait(false);
                    publishedSeq = result.PublishSeq;
                    return result;
                },
                result => string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PublishDone)
                        ?? "Опубликовано #{0}: {1} семейств, новых файлов: {2}.",
                    result.PublishSeq, result.ItemsCount, result.UploadedFiles),
                ct).ConfigureAwait(true);
            // Отмена публикации (publishedSeq == 0) — CloudLink не трогаем.
            if (failed || publishedSeq == 0) return;

            // Последний seq публикации уезжает в CloudLink (registry + database_meta).
            var fresh = _databaseManager.ListConnections().FirstOrDefault(c => c.Id == connection.Id);
            var freshLink = fresh?.CloudLink ?? link;
            await _databaseManager.SetCloudLinkAsync(connection.Id,
                freshLink with { LastSyncedPublishSeq = publishedSeq }, ct).ConfigureAwait(true);
            RefreshConnections();
            SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == connection.Id);
            HasCloudUpdates = false;
            NotifyCloudSelectionChanged();
        }
        finally
        {
            IsCloudBusy = false;
            PublishCloudChangesCommand.NotifyCanExecuteChanged();
        }
    }

    // ── «Обновить» = только pull (§7.3.3) ───────────────────────────────

    /// <summary>Вызывается из UpdateDatabaseAsync для Subscribed-баз (маршрутизация §7.3.3).</summary>
    private async Task<bool> UpdateSubscribedCloudAsync(DatabaseConnection connection, CancellationToken ct)
    {
        if (connection.CloudLink is not { } link) return false;

        IsCloudBusy = true;
        try
        {
            if (!await EnsureLoggedInAsync(link.Endpoint, ct).ConfigureAwait(true)) return true;

            var targetRoot = CloudPaths.SubscriptionRoot(link.Slug);
            var (status, failed) = await RunCloudOperationAsync(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PullTitle) ?? "Обновление из облака",
                (progress, token) => _cloudSync.SyncAsync(
                    // Имя базы сохраняем человеческое (не slug) — иначе apply переименует копию.
                    new CloudSyncRequest(link.Slug, targetRoot, connection.Name), progress, token),
                result => result.Updated
                    ? string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PullDone)
                            ?? "Обновлено до публикации #{0}: {1} семейств.",
                        result.PublishSeq, result.ItemsCount)
                    : string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_UpToDate)
                            ?? "База актуальна (публикация #{0})",
                        result.PublishSeq),
                ct).ConfigureAwait(true);
            if (failed || status is null) return true; // отмена — seq не записываем

            HasCloudUpdates = false;

            // Валидатор Ф5-6 P2: хвост метода остаётся на UI-потоке —
            // RefreshAccessAndLoadTreeAsync зовёт GetCurrentUser() (Revit API,
            // контракт «не переносить за await»).
            var remoteSeq = await _cloudSync.GetRemotePublishSeqAsync(link.Slug, ct).ConfigureAwait(true);
            var fresh = _databaseManager.ListConnections().FirstOrDefault(c => c.Id == connection.Id);
            if (fresh?.CloudLink is { } freshLink)
                await _databaseManager.SetCloudLinkAsync(connection.Id,
                    freshLink with { LastSyncedPublishSeq = remoteSeq }, ct).ConfigureAwait(true);

            // Копия на диске заменена целиком — активная подписная база требует перезагрузки дерева.
            if (_databaseManager.GetActiveConnection()?.Id == connection.Id)
            {
                _staleDetector.InvalidateCache();
                _complianceService.InvalidateCache();
                ResetAdvancedFilter();
                await RefreshAccessAndLoadTreeAsync().ConfigureAwait(true);
                InvalidateLoadAndPlaceCommands();
                await RefreshDatabaseUpdateStateAsync().ConfigureAwait(true);
            }
            RefreshConnections();
            SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == connection.Id);
            NotifyCloudSelectionChanged();
            return true;
        }
        finally
        {
            IsCloudBusy = false;
        }
    }

    // ── Приглашение (копирование) ───────────────────────────────────────

    private bool CanCopyCloudInvite => IsSelectedCloudPublished;

    [RelayCommand(CanExecute = nameof(CanCopyCloudInvite))]
    private void CopyCloudInvite()
    {
        if (SelectedConnection?.Connection.CloudLink is not { } link) return;
        try
        {
            System.Windows.Clipboard.SetText(CloudInvite.Build(link.Endpoint, link.Slug));
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_InviteCopied)
                ?? "Приглашение скопировано в буфер обмена";
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Clipboard.SetText failed: {ex.Message} [Action: скопируйте приглашение вручную]");
        }
    }

    // ── Бейдж «доступны обновления» ─────────────────────────────────────

    /// <summary>Фоновый check для Subscribed-баз: серверный seq vs sync-state.json копии.</summary>
    private async Task CheckCloudUpdatesAsync()
    {
        var link = SelectedConnection?.Connection.CloudLink;
        if (link?.Role != CloudLinkRole.Subscribed
            || !_cloudAuth.IsLoggedIn
            || !string.Equals(_cloudAuth.CurrentAccount?.Endpoint, link.Endpoint, StringComparison.OrdinalIgnoreCase))
        {
            SetHasCloudUpdatesOnUiThread(false);
            return;
        }

        try
        {
            var localSeq = await Task.Run(() => ReadLocalPublishSeq(link.Slug)).ConfigureAwait(true);
            var remoteSeq = await _cloudSync.GetRemotePublishSeqAsync(link.Slug).ConfigureAwait(false);
            var hasUpdates = remoteSeq is not null && (localSeq is null || remoteSeq > localSeq);
            SetHasCloudUpdatesOnUiThread(hasUpdates);
        }
        catch (Exception ex)
        {
            // Сервер недоступен — бейдж молча скрыт (§7.3.10: контент работает).
            SmartConLogger.Debug($"Cloud update check failed: {ex.Message}");
            SetHasCloudUpdatesOnUiThread(false);
        }
    }

    private static long? ReadLocalPublishSeq(string slug)
    {
        try
        {
            var path = CloudPaths.SyncStatePath(CloudPaths.SubscriptionRoot(slug));
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("publishSeq", out var seq) && seq.TryGetInt64(out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void SetHasCloudUpdatesOnUiThread(bool value)
    {
        if (_dispatcher.CheckAccess()) HasCloudUpdates = value;
        else _dispatcher.Invoke(() => HasCloudUpdates = value);
    }

    /// <summary>AccountChanged приходит из фоновых потоков (refresh/401) — маршалим на UI.</summary>
    private void OnCloudAccountChanged()
    {
        _dispatcher.Invoke(() =>
        {
            NotifyCloudSelectionChanged();
            _ = CheckCloudUpdatesAsync();
        });
    }

    // ── Общие шаги ──────────────────────────────────────────────────────

    /// <summary>Шаг аккаунта (§7.3.1): без входа дальше нельзя.</summary>
    private async Task<bool> EnsureLoggedInAsync(string? endpoint, CancellationToken ct)
    {
        if (_cloudAuth.IsLoggedIn
            && (endpoint is null
                || string.Equals(_cloudAuth.CurrentAccount?.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)))
            return true;

        var vm = new CloudLoginViewModel(_cloudAuth);
        if (!string.IsNullOrEmpty(endpoint)) vm.Endpoint = endpoint!;
        var accepted = _dialogService.ShowCloudLogin(vm) == true && vm.Success;
        if (!accepted) return false;

        // endpoint приглашения мог отличаться от аккаунта, под которым вошли.
        if (endpoint is not null
            && !string.Equals(_cloudAuth.CurrentAccount?.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            // Валидатор Ф5-6 P3: тихий отказ непонятен пользователю.
            SmartConLogger.Warn(
                $"Cloud account endpoint mismatch after login: expected '{endpoint}'. " +
                "[Action: войдите в аккаунт сервера, указанного в приглашении]");
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_OperationFailedTitle) ?? "Облачная операция",
                string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_LoginFailed) ?? "Операция не выполнена: {0}",
                    endpoint));
            return false;
        }
        return true;
    }

    /// <summary>Modeless-прогресс ADR-048: показать → запустить → дождаться закрытия.</summary>
    private async Task<(string? Status, bool Failed)> RunCloudOperationAsync(
        string title,
        Func<IProgress<CloudOperationProgress>, CancellationToken, Task<CloudPublishResult>> operation,
        Func<CloudPublishResult, string> successStatus,
        CancellationToken ct)
    {
        return await RunCloudOperationCoreAsync(title,
            async (progress, token) => successStatus(await operation(progress, token).ConfigureAwait(false)),
            ct).ConfigureAwait(true);
    }

    private async Task<(string? Status, bool Failed)> RunCloudOperationAsync(
        string title,
        Func<IProgress<CloudOperationProgress>, CancellationToken, Task<CloudSyncResult>> operation,
        Func<CloudSyncResult, string> successStatus,
        CancellationToken ct)
    {
        return await RunCloudOperationCoreAsync(title,
            async (progress, token) => successStatus(await operation(progress, token).ConfigureAwait(false)),
            ct).ConfigureAwait(true);
    }

    private async Task<(string? Status, bool Failed)> RunCloudOperationCoreAsync(
        string title,
        Func<IProgress<CloudOperationProgress>, CancellationToken, Task<string>> operation,
        CancellationToken ct)
    {
        var vm = new CloudOperationProgressViewModel(title, operation);
        _dialogService.ShowCloudOperationProgressDialog(vm);
        try
        {
            await vm.RunAsync().ConfigureAwait(true);
            await vm.DialogCompletion.ConfigureAwait(true);
            if (!vm.Failed && vm.FinalStatus is not null) StatusMessage = vm.FinalStatus;
            return (vm.FinalStatus, vm.Failed);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
