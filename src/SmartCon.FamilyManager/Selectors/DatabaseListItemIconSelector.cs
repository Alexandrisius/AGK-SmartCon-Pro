using System.Windows;
using System.Windows.Controls;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Selectors;

/// <summary>
/// Picks the <see cref="DataTemplate"/> for a <see cref="DatabaseListItem"/>
/// according to its cloud link / project-base match status so the ComboBox
/// shows the right icon: cloud bases get a CLOUD-only icon (like project
/// bases get a folder-only icon, owner decision 2026-09-11), local bases —
/// general / project-match / project-mismatch. See #119 + cloud §7.3.12.
/// </summary>
public sealed class DatabaseListItemIconSelector : DataTemplateSelector
{
    public DataTemplate? GeneralTemplate { get; set; }
    public DataTemplate? ProjectMatchTemplate { get; set; }
    public DataTemplate? ProjectMismatchTemplate { get; set; }
    public DataTemplate? CloudTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not DatabaseListItem dli) return base.SelectTemplate(item, container);

        // Облачная база — всегда только облачко (Published↑ / Subscribed↓),
        // ортогонально Kind (General/Project) и match-статусу.
        if (dli.Connection.CloudLink is not null) return CloudTemplate;

        if (dli.Kind == BaseType.General) return GeneralTemplate;
        return dli.MatchStatus switch
        {
            ProjectBaseMatchKind.Match => ProjectMatchTemplate,
            ProjectBaseMatchKind.Mismatch => ProjectMismatchTemplate,
            _ => GeneralTemplate
        };
    }
}

/// <summary>
/// VM-side wrapper around <see cref="DatabaseConnection"/> enriched with
/// the cached project-base match status so the ComboBox can show an
/// appropriate icon (general / match / mismatch) without re-evaluating the
/// binding on every render. Rebuilt by
/// <c>FamilyManagerMainViewModel.Database.RefreshConnections</c>.
/// </summary>
public sealed class DatabaseListItem
{
    public DatabaseConnection Connection { get; }
    public BaseType Kind => Connection.Kind;
    public ProjectBaseMatchKind MatchStatus { get; }
    public string Name => Connection.Name;
    public string? MismatchReason { get; }

    public DatabaseListItem(DatabaseConnection connection, ProjectBaseMatchKind matchStatus, string? mismatchReason = null)
    {
        Connection = connection;
        MatchStatus = matchStatus;
        MismatchReason = mismatchReason;
    }

    // ── Cloud catalog (срез v1, §7.3.12): значок-облачко Published↑/Subscribed↓ ──

    /// <summary>"published" / "subscribed" / "" — DataTrigger-ключ для оверлея в шаблонах.</summary>
    public string CloudBadge => Connection.CloudLink?.Role switch
    {
        CloudLinkRole.Published => "published",
        CloudLinkRole.Subscribed => "subscribed",
        _ => string.Empty
    };

    public string? CloudBadgeTooltip => Connection.CloudLink?.Role switch
    {
        CloudLinkRole.Published => LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_PublishedIconTooltip)
            ?? "Облачная база — публикуется на сервер",
        CloudLinkRole.Subscribed => LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_SubscribedIconTooltip)
            ?? "Облачная база — подписка (только чтение)",
        _ => null
    };

    public static implicit operator DatabaseConnection(DatabaseListItem dli) => dli.Connection;
}