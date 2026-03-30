using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace SmartCon.Revit.Extensions;

/// <summary>
/// Extension-методы для работы с Revit Element.
/// </summary>
public static class ElementExtensions
{
    /// <summary>
    /// Безопасно получить ConnectorManager элемента.
    /// Работает с FamilyInstance, MEPCurve (трубы, воздуховоды) и FlexPipe (гибкие трубы).
    /// Возвращает null если элемент не имеет коннекторов.
    /// </summary>
    public static ConnectorManager? GetConnectorManager(this Element element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element is FamilyInstance familyInstance)
        {
            return familyInstance.MEPModel?.ConnectorManager;
        }

        if (element is MEPCurve mepCurve)
        {
            return mepCurve.ConnectorManager;
        }

        if (element is FlexPipe flexPipe)
        {
            return flexPipe.ConnectorManager;
        }

        return null;
    }

    /// <summary>
    /// Проверка, имеет ли элемент хотя бы один свободный коннектор.
    /// Исключает ConnectorType.Curve (I-08).
    /// </summary>
    public static bool HasFreeConnectors(this Element element)
    {
        var cm = element.GetConnectorManager();

        if (cm is null)
        {
            return false;
        }

        return cm.GetFreeConnectors().Count > 0;
    }

    /// <summary>
    /// Проверка, является ли элемент MEP-элементом с коннекторами.
    /// </summary>
    public static bool IsMepElement(this Element element)
    {
        return element.GetConnectorManager() is not null;
    }
}
