namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Progress payload of a catalog compliance run (#259, «Проверить → Правила»).
/// Mirrors <see cref="StaleCheckProgress"/> for the rule-check flow: the
/// dockable pane renders it as the thin bottom progress bar plus the
/// «Проверка правил X из Y — имя» status text.
/// </summary>
/// <param name="Completed">Number of catalog items evaluated so far.</param>
/// <param name="Total">Total number of catalog items in the checked
/// category subtree.</param>
/// <param name="CurrentItemName">Display name of the item just evaluated.</param>
public sealed record ComplianceCheckProgress(
    int Completed,
    int Total,
    string CurrentItemName);
