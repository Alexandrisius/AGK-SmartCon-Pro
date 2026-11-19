using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// VM-side wrapper around <see cref="DatabaseConnection"/> enriched with
/// the cached project-base match status so the ComboBox can show an
/// appropriate icon (general / match / mismatch) without re-evaluating the
/// binding on every render. Rebuilt by
/// <c>FamilyManagerMainViewModel.Database.RefreshConnections</c>.
/// Lives in Core (depends only on Core types) so the SmartCon.UI selector and
/// the panel XAML can reference it without a WPF local-type markup compile pass.
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

    public static implicit operator DatabaseConnection(DatabaseListItem dli) => dli.Connection;
}