using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace SmartCon.App.Ribbon;

public static class RibbonBuilder
{
    private const string TabName = "SmartCon";
    private const string PanelPipeSystems = "Pipe Systems";
    private const string PanelProjectManagement = "Project Management";
    private const string PanelInfo = "Info";

    public static void CreateRibbon(UIControlledApplication app)
    {
        app.CreateRibbonTab(TabName);

        var appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var pipeConnectAssembly = Path.Combine(appDir, "SmartCon.PipeConnect.dll");

        // --- Pipe Systems Panel ---
        var psPanel = app.CreateRibbonPanel(TabName, PanelPipeSystems);

        var pipeConnectButton = new PushButtonData(
            name: "PipeConnect",
            text: "Pipe\nConnect",
            assemblyName: pipeConnectAssembly,
            className: "SmartCon.PipeConnect.Commands.PipeConnectCommand")
        {
            ToolTip = "MEP pipe element connection in 3D with two clicks",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.PipeCon_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.PipeCon_16x16.png")
        };

        psPanel.AddItem(pipeConnectButton);

        psPanel.AddSeparator();

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

        psPanel.AddItem(settingsButton);

        // --- Project Management Panel ---
        var pmPanel = app.CreateRibbonPanel(TabName, PanelProjectManagement);
        var pmAssembly = Path.Combine(appDir, "SmartCon.ProjectManagement.dll");

        var shareProjectButton = new PushButtonData(
            name: "ShareProject",
            text: "Export\nProject",
            assemblyName: pmAssembly,
            className: "SmartCon.ProjectManagement.Commands.ShareProjectCommand")
        {
            ToolTip = "Export Revit model to Shared zone (ISO 19650)",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.ShareProject_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.ShareProject_16x16.png")
        };

        pmPanel.AddItem(shareProjectButton);

        pmPanel.AddSeparator();

        var shareSettingsButton = new PushButtonData(
            name: "ShareSettings",
            text: "Settings",
            assemblyName: pmAssembly,
            className: "SmartCon.ProjectManagement.Commands.ShareSettingsCommand")
        {
            ToolTip = "Configure export settings",
            LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.Settings_32x32.png"),
            Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.Settings_16x16.png")
        };

        pmPanel.AddItem(shareSettingsButton);

        // --- Info Panel (always rightmost) ---
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
