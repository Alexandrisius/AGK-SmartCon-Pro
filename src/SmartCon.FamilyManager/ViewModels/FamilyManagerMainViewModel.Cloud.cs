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
    [ObservableProperty] private bool _hasUnpublishedCloudChanges;

    public bool IsSelectedCloudPublished =>
        SelectedConnection?.Connection.CloudLink?.Role == CloudLinkRole.Published;

    public bool IsSelectedCloudSubscribed =>
        SelectedConnection?.Connection.CloudLink?.Role == CloudLinkRole.Subscribed;

    /// <summary>Тултип кнопки ↻: базовый текст + приписка о доступных облачных обновлениях.</summary>
    public string RefreshButtonTooltip
    {
        get
        {
            var baseText = LanguageManager.GetString(StringLocalization.Keys.FM_RefreshTooltip) ?? "Обновить дерево";
            if (!HasCloudUpdates) return baseText;
            return baseText + (LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_RefreshBadgeSuffix) ?? string.Empty);
        }
    }

    private void NotifyCloudSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSelectedCloudPublished));
        OnPropertyChanged(nameof(IsSelectedCloudSubscribed));
        PublishCloudChangesCommand.NotifyCanExecuteChanged();
        CopyCloudInviteCommand.NotifyCanExecuteChanged();
        UnpublishCloudDatabaseCommand.NotifyCanExecuteChanged();
    }

    // ── Мастер «Создать облачную базу» (§7.3.1) ─────────────────────────

    [RelayCommand]
    private async Task OpenCloudDatabaseWizardAsync(CancellationToken ct)
    {
        var wizard = new CloudDatabaseWizardViewModel();
        if (_dialogService.ShowCloudDatabaseWizard(wizard) != true || !wizard.Accepted)
            return;

        using var _scope = SmartConLogger.BeginScope("CloudUI",
            ("Method", nameof(OpenCloudDatabaseWizardAsync)));
        try
        {
            await CreateEmptyCloudDatabaseAsync(wizard.DatabaseName.Trim(), ct).ConfigureAwait(true);
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

    /// <summary>
    /// «Подключить базу» → облачная ветка: приглашение → subscribe → первый
    /// pull → регистрация копии. Ошибки сети — диалогом, как у мастера.
    /// </summary>
    private async Task ConnectByInviteAsync(CloudInvite.InviteData invite)
    {
        using var _scope = SmartConLogger.BeginScope("CloudUI",
            ("Method", nameof(ConnectByInviteAsync)),
            ("Slug", invite.Slug));
        try
        {
            await SubscribeByInviteAsync(invite, CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Connect by invite failed: {ex.GetType().Name}: {ex.Message} [Action: проверьте строку приглашения, адрес сервера и сеть]");
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
            result => result.Updated
                ? string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_WizardSubscribed)
                        ?? "Подключено к каталогу «{0}»: получено {1} семейств.",
                    invite.Slug, result.AddedCount)
                : string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_UpToDate)
                        ?? "База актуальна (публикация #{0})",
                    result.PublishSeq),
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
            Models.Cloud.CatalogManifestV1? publishedManifest = null;
            var (_, failed) = await RunCloudOperationAsync(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PublishTitle) ?? "Публикация в облако",
                async (progress, token) =>
                {
                    var result = await _cloudPublish.PublishAsync(
                        new CloudPublishRequest(link.Slug, link.CatalogId), progress, token).ConfigureAwait(false);
                    publishedSeq = result.PublishSeq;
                    publishedManifest = result.Manifest;
                    return result;
                },
                result => string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PublishDone)
                        ?? "Опубликовано #{0}: {1} семейств, новых файлов: {2}.",
                    result.PublishSeq, result.ItemsCount, result.UploadedFiles),
                ct).ConfigureAwait(true);
            // Отмена публикации (publishedSeq == 0) — CloudLink не трогаем.
            if (failed || publishedSeq == 0) return;

            // Дайджест опубликованного контента — для точки «есть непубликованные
            // изменения» на шестерёнке (владелец 2026-09-11: «не забывать публиковать»).
            _cloudPublishState.RecordPublished(link.Slug, publishedManifest!);
            HasUnpublishedCloudChanges = false;

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

    // ── «Снять с публикации» (§7.3.2, срез: tombstone без sunsetting-окна) ─

    private bool CanUnpublishCloudDatabase => IsSelectedCloudPublished && !IsCloudBusy;

    [RelayCommand(CanExecute = nameof(CanUnpublishCloudDatabase))]
    private async Task UnpublishCloudDatabaseAsync(CancellationToken ct)
    {
        var connection = SelectedConnection?.Connection;
        if (connection?.CloudLink is not { } link) return;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_UnpublishTitle) ?? "Снять с публикации",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_UnpublishBody)
                    ?? "Каталог «{1}» будет удалён с сервера.",
                connection.Name, link.Slug));
        if (!confirmed) return;

        IsCloudBusy = true;
        UnpublishCloudDatabaseCommand.NotifyCanExecuteChanged();
        try
        {
            if (!await EnsureLoggedInAsync(link.Endpoint, ct).ConfigureAwait(true)) return;

            using var _scope = SmartConLogger.BeginScope("CloudUI",
                ("Method", nameof(UnpublishCloudDatabaseAsync)),
                ("Slug", link.Slug));
            await _cloudApi.UnpublishCatalogAsync(link.Slug, ct).ConfigureAwait(true);
            await _databaseManager.SetCloudLinkAsync(connection.Id, null, ct).ConfigureAwait(true);
            _cloudPublishState.Clear(link.Slug);
            SmartConLogger.Info($"Catalog unpublished: '{link.Slug}' — local base stays, link cleared");

            RefreshConnections();
            SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == connection.Id);
            HasCloudUpdates = false;
            HasUnpublishedCloudChanges = false;
            NotifyCloudSelectionChanged();
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_UnpublishDone)
                    ?? "База «{0}» снята с публикации. Локальная база осталась — теперь она обычная.",
                connection.Name);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Unpublish failed: {ex.GetType().Name}: {ex.Message} [Action: проверьте подключение к серверу и повторите]");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_UnpublishTitle) ?? "Снять с публикации",
                ex.Message);
        }
        finally
        {
            IsCloudBusy = false;
            UnpublishCloudDatabaseCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Авто-снятие с публикации при удалении локальной Published-базы (решение
    /// владельца 2026-09-11): без него удаление оставляет orphan-каталог на
    /// сервере — slug занят навсегда, подписчики продолжают получать копию.
    /// true = можно удалять локальную базу; false = пользователь отменил.
    /// </summary>
    private async Task<bool> TryUnpublishBeforeLocalDeleteAsync(DatabaseConnection connection)
    {
        if (connection.CloudLink is not { Role: CloudLinkRole.Published } link)
            return true;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeletePublishedTitle) ?? "База опубликована в облаке",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeletePublishedBody)
                    ?? "База «{0}» опубликована как каталог «{1}».",
                connection.Name, link.Slug));
        if (!confirmed) return false;

        using var _scope = SmartConLogger.BeginScope("CloudUI",
            ("Method", nameof(TryUnpublishBeforeLocalDeleteAsync)),
            ("Slug", link.Slug));
        try
        {
            if (!await EnsureLoggedInAsync(link.Endpoint, CancellationToken.None).ConfigureAwait(true))
                return false;
            await _cloudApi.UnpublishCatalogAsync(link.Slug, CancellationToken.None).ConfigureAwait(true);
            _cloudPublishState.Clear(link.Slug);
            SmartConLogger.Info($"Catalog auto-unpublished before local delete: '{link.Slug}'");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Auto-unpublish before delete failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: проверьте сервер; каталог останется на сервере, если продолжить]");
            return _dialogService.ShowConfirmation(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeleteUnpublishFailedTitle)
                    ?? "Не удалось снять базу с публикации",
                string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeleteUnpublishFailedBody)
                        ?? "{0}\r\n\r\nКаталог останется на сервере. Всё равно удалить локальную базу?",
                    ex.Message));
        }
    }

    // ── «Обновить» = только pull (§7.3.3) ───────────────────────────────

    /// <summary>
    /// Тихий pull для активной подписной базы перед перезагрузкой дерева
    /// (кнопка ↻ панели): без сессии/сервера — просто дерево; без новой
    /// публикации — ничего не показывает; при наличии — modeless-прогресс и
    /// итог «Обновлено до #N». Никогда push.
    /// </summary>
    private async Task PullSubscribedCloudIfAvailableAsync()
    {
        if (IsCloudBusy) return;
        var active = _databaseManager.GetActiveConnection();
        if (active?.CloudLink is not { Role: CloudLinkRole.Subscribed } link) return;
        if (!_cloudAuth.IsLoggedIn
            || !string.Equals(_cloudAuth.CurrentAccount?.Endpoint, link.Endpoint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var remoteSeq = await _cloudSync.GetRemotePublishSeqAsync(link.Slug).ConfigureAwait(true);
            var localSeq = ReadLocalPublishSeq(link.Slug);
            SmartConLogger.Debug($"Cloud update check on refresh: slug={link.Slug} remoteSeq={remoteSeq?.ToString() ?? "null"} localSeq={localSeq?.ToString() ?? "null"}");
            if (remoteSeq is null || (localSeq is not null && remoteSeq <= localSeq))
            {
                HasCloudUpdates = false;
                return;
            }
        }
        catch (CloudApiException ex) when (IsCatalogGone(ex))
        {
            // Каталог снят с публикации автором (410, tombstone): подписчик должен
            // узнать об этом с ↻, а не молча не получать обновления (владелец 2026-09-11).
            HasCloudUpdates = false;
            NotifyCatalogGone(active.Name, link.Slug);
            await ConvertGoneCopyToLocalAsync(active).ConfigureAwait(true);
            return;
        }
        catch (Exception ex)
        {
            // Сервер недоступен — обновление дерева не должно падать (§7.3.10).
            SmartConLogger.Debug($"Cloud update check on refresh failed: {ex.Message}");
            return;
        }

        await UpdateSubscribedCloudAsync(active, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Pull подписной базы с modeless-прогрессом (кнопка ↻ панели, §7.3.3).</summary>
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
                            ?? "Обновлено до публикации #{0}: {1}.",
                        result.PublishSeq, FormatSyncDelta(result))
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

    /// <summary>
    /// Человекочитаемая дельта синхронизации: «добавлено 2, обновлено 1, удалено 3»
    /// (только ненулевые части; 0/0/0 при изменившемся seq = правки настроек каталога).
    /// Стресс-тест 2026-09-11: полный счётчик выдавал «добавлено 8» при удалении.
    /// </summary>
    private static string FormatSyncDelta(CloudSyncResult result)
    {
        var parts = new List<string>(3);
        if (result.AddedCount > 0)
            parts.Add(string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeltaAdded) ?? "добавлено {0}",
                result.AddedCount));
        if (result.UpdatedCount > 0)
            parts.Add(string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeltaUpdated) ?? "обновлено {0}",
                result.UpdatedCount));
        if (result.RemovedCount > 0)
            parts.Add(string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeltaRemoved) ?? "удалено {0}",
                result.RemovedCount));
        if (parts.Count == 0)
            return LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_DeltaNone)
                ?? "изменения в настройках каталога";
        return string.Join(", ", parts);
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

    // ── Точка «есть локальные непубликованные изменения» (шестерёнка) ──

    /// <summary>
    /// Контент активной Published-базы отличается от последней публикации →
    /// янтарная точка на шестерёнке (владелец 2026-09-11: «не забывать
    /// публиковать изменения после локальных изменений»). Дайджест манифеста;
    /// «с этой машины не публиковали» — точка тоже горит (кроме пустого
    /// каталога). Вызывается из LoadTreeAsync (общий финал любой мутации
    /// контента) — троттл 3с + in-flight guard, чтобы поиск не гонял
    /// пересчёт на каждое дерево.
    /// </summary>
    private int _unpublishedCheckInFlight;
    private int _lastUnpublishedCheckMs;

    private async Task RefreshUnpublishedCloudChangesAsync()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _unpublishedCheckInFlight, 1, 0) != 0)
            return;
        try
        {
            var nowMs = Environment.TickCount;
            if (nowMs - System.Threading.Volatile.Read(ref _lastUnpublishedCheckMs) < 3000)
                return;
            System.Threading.Volatile.Write(ref _lastUnpublishedCheckMs, nowMs);

            var active = _databaseManager.GetActiveConnection();
            if (active?.CloudLink is not { Role: CloudLinkRole.Published } link)
            {
                SetHasUnpublishedOnUiThread(false);
                return;
            }
            var hasChanges = await _cloudPublishState.HasUnpublishedChangesAsync(link.Slug, link.CatalogId)
                .ConfigureAwait(true);
            SmartConLogger.Debug($"Unpublished-changes check: slug={link.Slug} hasChanges={hasChanges}");
            SetHasUnpublishedOnUiThread(hasChanges);
        }
        catch (Exception ex)
        {
            // Локальная проверка не должна мешать панели (§7.3.10).
            SmartConLogger.Debug($"Unpublished-changes check failed: {ex.Message}");
            SetHasUnpublishedOnUiThread(false);
        }
        finally
        {
            System.Threading.Volatile.Write(ref _unpublishedCheckInFlight, 0);
        }
    }

    private void SetHasUnpublishedOnUiThread(bool value)
    {
        if (_dispatcher.CheckAccess()) HasUnpublishedCloudChanges = value;
        else _dispatcher.Invoke(() => HasUnpublishedCloudChanges = value);
    }

    // ── Бейдж «доступны обновления» ─────────────────────────────────────

    /// <summary>Фоновый check для Subscribed-баз: серверный seq vs sync-state.json копии.</summary>
    private async Task CheckCloudUpdatesAsync()
    {
        var connection = SelectedConnection?.Connection;
        var link = connection?.CloudLink;
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
        catch (CloudApiException ex) when (IsCatalogGone(ex))
        {
            SetHasCloudUpdatesOnUiThread(false);
            var name = connection?.Name ?? link.Slug;
            if (_dispatcher.CheckAccess())
                NotifyCatalogGone(name, link.Slug);
            else
                _dispatcher.Invoke(() => NotifyCatalogGone(name, link.Slug));
            // Текст уведомления обещает конвертацию — конвертируем и здесь
            // (SetCloudLinkAsync по id безопасен и для не-активной базы).
            if (connection is not null)
                _ = ConvertGoneCopyToLocalAsync(connection);
        }
        catch (Exception ex)
        {
            // Сервер недоступен — бейдж молча скрыт (§7.3.10: контент работает).
            SmartConLogger.Debug($"Cloud update check failed: {ex.Message}");
            SetHasCloudUpdatesOnUiThread(false);
        }
    }

    /// <summary>410 catalog_unpublished — каталог снят с публикации автором (tombstone сервера).</summary>
    private static bool IsCatalogGone(CloudApiException ex) =>
        ex.StatusCode == 410 && string.Equals(ex.Code, "catalog_unpublished", StringComparison.Ordinal);

    /// <summary>
    /// Подписная копия осиротела (каталог снят с публикации): конвертируем в
    /// обычную локальную базу — CloudLink очищается и в registry, и в
    /// database_meta копии (иначе self-heal воскресит связь при следующем
    /// «Подключить базу»), гейт записи снимается, значок перестаёт быть
    /// облачным (владелец 2026-09-11: «не смущать облаком»).
    /// </summary>
    private async Task ConvertGoneCopyToLocalAsync(DatabaseConnection connection)
    {
        using var _scope = SmartConLogger.BeginScope("CloudUI",
            ("Method", nameof(ConvertGoneCopyToLocalAsync)),
            ("Slug", connection.CloudLink?.Slug ?? ""));
        try
        {
            await _databaseManager.SetCloudLinkAsync(connection.Id, null).ConfigureAwait(true);
            SmartConLogger.Info("Subscribed copy converted to local: catalog unpublished on the server (410)");

            RefreshConnections();
            SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == connection.Id);
            HasUnpublishedCloudChanges = false;
            NotifyCloudSelectionChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Convert gone copy to local failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: перезапустите Revit — копия останется облачной до следующего ↻]");
        }
    }

    /// <summary>Уведомление подписчику о снятии каталога с публикации (строка статуса панели).</summary>
    private void NotifyCatalogGone(string databaseName, string slug)
    {
        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_CatalogGone)
                ?? "Каталог «{1}» снят с публикации автором — обновления базы «{0}» недоступны.",
            databaseName, slug);
    }

    /// <summary>
    /// Локальный seq подписной копии — через CloudSyncService (тот же
    /// сериализатор sync-state.json, что пишет pull). Ручной парсинг JSON тут
    /// словил расхождение camelCase/PascalCase ключа → вечный бейдж.
    /// </summary>
    private long? ReadLocalPublishSeq(string slug) =>
        _cloudSync.ReadLocalPublishSeq(CloudPaths.SubscriptionRoot(slug));

    partial void OnHasCloudUpdatesChanged(bool value) => OnPropertyChanged(nameof(RefreshButtonTooltip));

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

    /// <summary>
    /// Modeless-прогресс ADR-048: показать → запустить → дождаться ОПЕРАЦИИ.
    /// Диалог сводки может оставаться открытым (пользователь читает) — управление
    /// возвращается сразу по завершении операции, чтобы IsCloudBusy не держал
    /// publish/unpublish серыми, пока открыт диалог (ретест 2026-09-11: pull падал
    /// на заблокированном семействе, диалог сводки ждал клика, «Снять с публикации»
    /// был недоступен). Dispose — фоновой цепочкой после закрытия окна.
    /// </summary>
    private async Task<(string? Status, bool Failed)> RunCloudOperationCoreAsync(
        string title,
        Func<IProgress<CloudOperationProgress>, CancellationToken, Task<string>> operation,
        CancellationToken ct)
    {
        var vm = new CloudOperationProgressViewModel(title, operation);
        _dialogService.ShowCloudOperationProgressDialog(vm);
        await vm.RunAsync().ConfigureAwait(true);
        if (!vm.Failed && vm.FinalStatus is not null) StatusMessage = vm.FinalStatus;
        var result = (vm.FinalStatus, vm.Failed);
        _ = DisposeWhenClosedAsync(vm);
        return result;
    }

    private static async Task DisposeWhenClosedAsync(CloudOperationProgressViewModel vm)
    {
        try
        {
            await vm.DialogCompletion.ConfigureAwait(false);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
