using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyMetadataEditViewModel : ObservableObject, IObservableRequestClose
{
    private readonly string _catalogItemId;
    private readonly IWritableFamilyCatalogProvider _writableProvider;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _category;
    [ObservableProperty] private string _tagsText = string.Empty;
    [ObservableProperty] private ContentStatus _contentStatus;

    public IReadOnlyList<ContentStatus> AvailableStatuses { get; } =
        Enum.GetValues(typeof(ContentStatus)).Cast<ContentStatus>().ToArray();

    public event Action<bool?>? RequestClose;

    public FamilyMetadataEditViewModel(
        string catalogItemId,
        string name,
        string? description,
        string? category,
        IReadOnlyList<string> tags,
        ContentStatus contentStatus,
        IWritableFamilyCatalogProvider writableProvider)
    {
        _catalogItemId = catalogItemId;
        _writableProvider = writableProvider;

        Name = name;
        Description = description;
        Category = category;
        TagsText = tags is not null && tags.Count > 0 ? string.Join(", ", tags) : string.Empty;
        ContentStatus = contentStatus;
    }

    [RelayCommand]
    private async Task Save()
    {
        var tags = TagsText
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        await _writableProvider.UpdateItemAsync(
            _catalogItemId,
            Name,
            Description,
            Category,
            tags,
            ContentStatus);

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);
}
