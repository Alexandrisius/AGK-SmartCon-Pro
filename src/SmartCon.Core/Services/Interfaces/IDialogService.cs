using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Абстракция для открытия окон из ViewModel (MVVM, I-10).
/// Реализация: SmartCon.PipeConnect/Services/PipeConnectDialogService.cs
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Открыть MiniTypeSelector рядом с курсором. Возвращает выбранный тип или null.
    /// </summary>
    ConnectorTypeDefinition? ShowMiniTypeSelector(
        IReadOnlyList<ConnectorTypeDefinition> availableTypes);

    /// <summary>
    /// Открыть редактор семейств фитингов с приоритетами.
    /// Возвращает список семейств или null если отменено.
    /// </summary>
    IEnumerable<FittingMapping>? ShowFamilySelector(
        IEnumerable<FittingMapping> currentFamilies,
        IReadOnlyList<string> availableFamilyNames);

    /// <summary>
    /// Показать предупреждение пользователю.
    /// </summary>
    void ShowWarning(string title, string message);
}
