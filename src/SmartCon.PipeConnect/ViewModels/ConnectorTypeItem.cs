using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Редактируемая обёртка ConnectorTypeDefinition для DataGrid в MappingEditorView.
/// </summary>
public sealed partial class ConnectorTypeItem : ObservableObject
{
    [ObservableProperty]
    private int _code;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    public static ConnectorTypeItem From(ConnectorTypeDefinition d) =>
        new() { Code = d.Code, Name = d.Name, Description = d.Description };

    public ConnectorTypeDefinition ToDefinition() =>
        new() { Code = Code, Name = Name, Description = Description };
}
