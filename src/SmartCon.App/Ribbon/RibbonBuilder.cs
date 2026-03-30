using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace SmartCon.App.Ribbon;

/// <summary>
/// Создаёт вкладку SmartCon и кнопки на ленте Revit.
/// Иконки загружаются из embedded resources (Build Action = EmbeddedResource).
/// </summary>
public static class RibbonBuilder
{
    private const string TabName = "SmartCon";
    private const string PanelName = "PipeConnect";

    public static void CreateRibbon(UIControlledApplication app)
    {
        app.CreateRibbonTab(TabName);

        var panel = app.CreateRibbonPanel(TabName, PanelName);

        var appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var pipeConnectAssembly = Path.Combine(appDir, "SmartCon.PipeConnect.dll");

        var pipeConnectButton = new PushButtonData(
            name: "PipeConnect",
            text: "PipeConnect",
            assemblyName: pipeConnectAssembly,
            className: "SmartCon.PipeConnect.Commands.PipeConnectCommand")
        {
            ToolTip = "Соединение трубных MEP-элементов двумя кликами",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.PipeCon_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.PipeCon_16x16.png")
        };

        panel.AddItem(pipeConnectButton);

        panel.AddSeparator();

        var settingsButton = new PushButtonData(
            name: "SmartConSettings",
            text: "Настройки",
            assemblyName: pipeConnectAssembly,
            className: "SmartCon.PipeConnect.Commands.SettingsCommand")
        {
            ToolTip = "Настройки SmartCon — типы коннекторов и маппинг фитингов",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.Settings_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.Settings_16x16.png")
        };

        panel.AddItem(settingsButton);
    }

    /// <summary>
    /// Загружает PNG-иконку из embedded resource сборки.
    /// Имя ресурса: "{RootNamespace}.{путь через точки}", например
    /// "SmartCon.App.Resources.Icons.PipeCon_32x32.png".
    /// </summary>
    private static BitmapSource? GetEmbeddedImage(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            return null;
        }

        return BitmapFrame.Create(stream);
    }
}
