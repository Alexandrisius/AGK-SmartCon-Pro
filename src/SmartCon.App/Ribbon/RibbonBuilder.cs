using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace SmartCon.App.Ribbon;

public static class RibbonBuilder
{
    private const string TabName = "SmartCon";
    private const string PanelPipeConnect = "PipeSystems";
    private const string PanelInfo = "Info";

    public static void CreateRibbon(UIControlledApplication app)
    {
        app.CreateRibbonTab(TabName);

        var appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var pipeConnectAssembly = Path.Combine(appDir, "SmartCon.PipeConnect.dll");

        // --- PipeConnect Panel ---
        var pcPanel = app.CreateRibbonPanel(TabName, PanelPipeConnect);

        var pipeConnectButton = new PushButtonData(
            name: "PipeConnect",
            text: "PipeConnect",
            assemblyName: pipeConnectAssembly,
            className: "SmartCon.PipeConnect.Commands.PipeConnectCommand")
        {
            ToolTip = "MEP pipe element connection in 3D with two clicks",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.PipeCon_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.PipeCon_16x16.png")
        };

        pcPanel.AddItem(pipeConnectButton);

        pcPanel.AddSeparator();

        var settingsButton = new PushButtonData(
            name: "SmartConSettings",
            text: "Settings",
            assemblyName: pipeConnectAssembly,
            className: "SmartCon.PipeConnect.Commands.SettingsCommand")
        {
            ToolTip = "SmartCon settings - connector types and fitting mapping",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.Settings_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.Settings_16x16.png")
        };

        pcPanel.AddItem(settingsButton);

        // --- Info Panel (rightmost, technical) ---
        var infoPanel = app.CreateRibbonPanel(TabName, PanelInfo);

        var aboutButton = new PushButtonData(
            name: "SmartConAbout",
            text: "About",
            assemblyName: pipeConnectAssembly,
            className: "SmartCon.PipeConnect.Commands.AboutCommand")
        {
            ToolTip = "About SmartCon - version, author, update check",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.Info_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.Info_16x16.png")
        };

        infoPanel.AddItem(aboutButton);
    }

    private static BitmapSource? GetEmbeddedImage(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
            return null;

        return BitmapFrame.Create(stream);
    }
}
