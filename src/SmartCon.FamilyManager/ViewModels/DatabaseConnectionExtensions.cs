using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

internal static class DatabaseConnectionExtensions
{
    /// <summary>
    /// Compares two <see cref="DatabaseConnection"/> instances by their
    /// <see cref="DatabaseConnection.Id"/>. Used by the FamilyManager VM
    /// to pick the icons for non-active rows without re-querying the
    /// database manager.
    /// </summary>
    public static bool ConnectionEquals(this DatabaseConnection? left, DatabaseConnection? right)
    {
        if (left is null) return right is null;
        if (right is null) return false;
        return string.Equals(left.Id, right.Id, System.StringComparison.Ordinal);
    }
}