using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class FieldLibraryViewModel : ObservableObject, IObservableRequestClose
{
    private readonly IDialogPresenter _dialogPresenter;

    public ObservableCollection<FieldDefinitionItem> Fields { get; } = [];

    [ObservableProperty]
    private int _selectedIndex = -1;

    private Window? _ownerWindow;

    public void SetOwnerWindow(Window? window) => _ownerWindow = window;

    public event Action<bool?>? RequestClose;

    public FieldLibraryViewModel(IDialogPresenter dialogPresenter)
    {
        _dialogPresenter = dialogPresenter;
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        foreach (var field in Fields)
            field.RefreshValidationModeDisplay();
    }

    private void Unsubscribe()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    [RelayCommand]
    private void AddField()
    {
        var item = new FieldDefinitionItem { Name = "new_field", DisplayName = "New Field" };
        Fields.Add(item);
        SelectedIndex = Fields.Count - 1;
    }

    [RelayCommand]
    private void RemoveField()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Fields.Count)
            Fields.RemoveAt(SelectedIndex);
    }

    [RelayCommand]
    private void DuplicateField()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Fields.Count) return;

        var source = Fields[SelectedIndex];
        var copy = new FieldDefinitionItem
        {
            Name = source.Name + "_copy",
            DisplayName = source.DisplayName,
            Description = source.Description,
            ValidationMode = source.ValidationMode,
            AllowedValues = [.. source.AllowedValues],
            MinLength = source.MinLength,
            MaxLength = source.MaxLength
        };
        Fields.Insert(SelectedIndex + 1, copy);
        SelectedIndex = SelectedIndex + 1;
    }

    [RelayCommand]
    private void OpenAllowedValues()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Fields.Count) return;

        var fieldItem = Fields[SelectedIndex];

        var originalMode = fieldItem.ValidationMode;
        var originalMin = fieldItem.MinLength;
        var originalMax = fieldItem.MaxLength;
        var originalValues = fieldItem.AllowedValues.ToList();

        var vm = new AllowedValuesViewModel(fieldItem);

        bool? dialogResult = null;
        vm.RequestClose += result => dialogResult = result;
        _dialogPresenter.ShowDialog(vm);

        if (dialogResult == true)
        {
            vm.ApplyTo(fieldItem);
        }
        else
        {
            fieldItem.ValidationMode = originalMode;
            fieldItem.MinLength = originalMin;
            fieldItem.MaxLength = originalMax;
            fieldItem.AllowedValues = originalValues;
        }
    }

    [RelayCommand]
    private void Ok()
    {
        Unsubscribe();
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Unsubscribe();
        RequestClose?.Invoke(false);
    }
}
