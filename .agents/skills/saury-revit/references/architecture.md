# Architecture Reference

## Technology Stack

| Technology | Version | Purpose |
|---|---|---|
| .NET | 8.0 (net8.0-windows, x64) | Runtime |
| WPF | Built-in | UI Framework |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM Source Generators |
| Microsoft.Extensions.Hosting | 9.0.4 | Dependency Injection + Lifecycle Management |
| Serilog | 9.0.0 | File Logging |
| Tuna.Revit.Extensions | 2026.0.20 | Revit API Helper Library |

## Complete Workflow for Adding New Features

Using "Wall Analyzer" as example, strictly follow A→F sequence.

### A. Create Model

File: `Models/WallAnalysisResult.cs`

```csharp
namespace <ProjectName>.Models;

public class WallAnalysisResult
{
    public string WallType { get; set; } = string.Empty;
    public double TotalArea { get; set; }
}
```

### B. Create ViewModel

File: `ViewModels/WallAnalyzerViewModel.cs`

```csharp
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace <ProjectName>.ViewModels;

public partial class WallAnalyzerViewModel : ObservableObject
{
    private readonly UIApplication? _uiApp;

    public WallAnalyzerViewModel(UIApplication? uiApp = null)
    {
        _uiApp = uiApp;
    }

    [ObservableProperty]
    private string result = string.Empty;

    [RelayCommand]
    private void Analyze()
    {
        // Business logic
    }
}
```

**Rules**:
- Class must be `partial` (source generator requirement)
- `[ObservableProperty]` on private field → auto-generates public property
- `[RelayCommand]` on method → auto-generates `ICommand`
- Constructor inject `UIApplication` for Revit API access
- Never put UI code in ViewModel (MessageBox, Window)

### C. Create View

File: `Views/WallAnalyzerView.xaml`

```xml
<Window x:Class="<ProjectName>.Views.WallAnalyzerView"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        xmlns:vm="clr-namespace:<ProjectName>.ViewModels"
        d:DataContext="{d:DesignInstance Type=vm:WallAnalyzerViewModel}"
        Title="Wall Analyzer" Width="400" Height="300"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize"
        mc:Ignorable="d">
    <Grid Margin="20">
        <TextBlock Text="{Binding Result}" />
    </Grid>
</Window>
```

File: `Views/WallAnalyzerView.xaml.cs`

```csharp
using System.Windows;

namespace <ProjectName>.Views;

public partial class WallAnalyzerView : Window
{
    public WallAnalyzerView(ViewModels.WallAnalyzerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
```

**Rules**:
- Constructor inject ViewModel, set `DataContext = viewModel`
- Use `d:DesignInstance` for designer type hints
- Never write business logic in code-behind
- Styles go in `Resources/Styles/`, never inline

### D. Create Command

File: `Commands/WallAnalyzerCommand.cs`

```csharp
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace <ProjectName>.Commands;

[Transaction(TransactionMode.Manual)]
public class WallAnalyzerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var view = Host.GetService<Views.WallAnalyzerView>();
        view.ShowDialog();
        return Result.Succeeded;
    }
}
```

**Rules**:
- Must have `[Transaction(TransactionMode.Manual)]`
- Get View from DI via `Host.GetService<T>()`
- `ShowDialog()` shows modal window

### E. Register in DI Container

Add at comment `[4]` in `Host.cs`:

```csharp
//[4] Add services like View, ViewModel, Service
builder.Services.AddTransient<ViewModels.WallAnalyzerViewModel>();
builder.Services.AddTransient<Views.WallAnalyzerView>();
```

**Rules**:
- View / ViewModel use `AddTransient` (new instance each time)
- Stateful services use `AddSingleton`
- `UIApplication` already registered as Singleton

### F. Add Ribbon Button

Add in `Application.cs` `CreateRibbon` method:

```csharp
panel.AddPushButton<WallAnalyzerCommand>(button =>
{
    button.LargeImage = new BitmapImage(
        new Uri("pack://application:,,,/<ProjectName>;component/Resources/Icons/wall-analyzer.png"));
    button.ToolTip = "Wall Analyzer";
    button.Title = "Wall Analysis";
});
```

**Rules**:
- Icons go in `Resources/Icons/`, Build Action = `Resource`
- Pack URI: `pack://application:,,,/<AssemblyName>;component/<Path>`
- Use `Tuna.Revit.Extensions` API

## Logging

```csharp
var logger = Host.GetService<ILogger<YourClass>>();
logger.LogInformation("Message {Parameter}", value);
```

Log location: `<Plugin DLL directory>/Logs/<ProjectName>.log` (daily rotation).

## File Modification Quick Reference

| What to do | Which files to modify |
|---|---|
| Add new feature | Model + ViewModel + View + Command + `Host.cs` + `Application.cs` |
| Add service | `Services/` + `Services/Interfaces/` + `Host.cs` |
| Add style | `Resources/Styles/` + View reference or merged resource dictionary |
| Add icon | `Resources/Icons/` (Build Action = Resource) |
| Modify logging | `appsettings.json` + `Host.cs` |
| Add NuGet package | `<ProjectName>.csproj` |
| Add config | `appsettings.json` + Options class + `Host.cs` registration |

## Build Troubleshooting

- **Property not found** → `dotnet clean && dotnet build --configuration Debug_R26` (source generator cache issue)
- **Wrong build config** → Only use `Debug_R26` / `Release_R26`
- **Platform error** → Must be x64, Revit is 64-bit process
