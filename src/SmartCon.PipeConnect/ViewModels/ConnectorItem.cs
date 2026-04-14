using SmartCon.Core.Models;
using SmartCon.Core;

using static SmartCon.Core.Units;
namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Элемент выпадающего списка коннекторов в PipeConnectEditorView.
/// Представляет один свободный коннектор динамического элемента.
/// </summary>
public sealed class ConnectorItem
{
    public ConnectorProxy Proxy { get; }

    public string DisplayName { get; }

    public ConnectorItem(ConnectorProxy proxy, int displayIndex)
    {
        Proxy = proxy;
        var dnMm = (int)System.Math.Round(proxy.Radius * 2.0 * FeetToMm);
        var typeLabel = proxy.ConnectionTypeCode.IsDefined
            ? $" [{proxy.ConnectionTypeCode.Value}]"
            : "";
        DisplayName = $"Коннектор {displayIndex}{typeLabel}  DN{dnMm}";
    }
}
