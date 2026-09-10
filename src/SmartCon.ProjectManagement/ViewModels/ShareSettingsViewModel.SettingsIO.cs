using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ShareSettingsViewModel
{
    [RelayCommand]
    private void OpenFieldLibrary()
    {
        try
        {
            SmartConLogger.Info("OpenFieldLibrary: creating ViewModel");
            var vm = new FieldLibraryViewModel(_dialogPresenter);
            foreach (var fd in FieldLibrary)
                vm.Fields.Add(FieldDefinitionItem.FromModel(fd));

            bool? dialogResult = null;
            vm.RequestClose += result => dialogResult = result;

            SmartConLogger.Info($"OpenFieldLibrary: {vm.Fields.Count} fields, calling ShowDialog via presenter");
            _dialogPresenter.ShowDialog(vm);
            SmartConLogger.Info("OpenFieldLibrary: ShowDialog returned");

            if (dialogResult != true) return;

            FieldLibrary.Clear();
            foreach (var item in vm.Fields)
                FieldLibrary.Add(item.ToModel());
            OnPropertyChanged(nameof(FieldNames));

            AutoParseFileName();
            RefreshValidation();
            RefreshMappingValidation();
            RefreshPreview();

            SmartConLogger.Info($"FieldLibrary updated: {FieldLibrary.Count} definitions");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenFieldLibrary failed:\n{ex}");
            _dialogService.ShowError("Error", $"OpenFieldLibrary error:\n\n{ex.Message}\n\n{ex.StackTrace}");
        }
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            Title = "Import Settings from File"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var settings = _repository.ImportFromJson(json);
            LoadFromSettings(settings);
            SmartConLogger.Info($"Imported settings from {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Import failed: {ex.Message}");
            _dialogService.ShowError("Error", $"Import failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ExportSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = "smartcon-export-settings.json",
            Title = "Export Settings to File"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var settings = BuildSettings();
            var json = _repository.ExportToJson(settings);
            File.WriteAllText(dialog.FileName, json);
            SmartConLogger.Info($"Exported settings to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Export failed: {ex.Message}");
            _dialogService.ShowError("Error", $"Export failed: {ex.Message}");
        }
    }

    private void LoadFromSettings(ShareProjectSettings settings)
    {
        ShareFolderPath = settings.ShareFolderPath;
        SyncBeforeShare = settings.SyncBeforeShare;

        ApplyPurgeOptions(settings.PurgeOptions);

        FieldLibrary.Clear();
        foreach (var fd in settings.FieldLibrary)
            FieldLibrary.Add(fd);
        OnPropertyChanged(nameof(FieldNames));

        LoadBlocksFromSettings(settings.FileNameTemplate);
        LoadExportMappingsFromSettings(settings.FileNameTemplate);

        _pendingKeepViewNames = settings.KeepViewNames.ToHashSet();

        if (Views.Count > 0)
        {
            var savedViewNames = settings.KeepViewNames.ToHashSet();
            foreach (var v in Views)
                v.IsSelected = savedViewNames.Contains(v.Name);
            OnPropertyChanged(nameof(SelectedCount));
        }

        AutoParseFileName();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (string.IsNullOrEmpty(CurrentFileName))
        {
            PreviewCurrent = PreviewShared = string.Empty;
            return;
        }

        PreviewCurrent = CurrentFileName;

        var template = BuildTemplate();
        var shared = _fileNameParser.TransformForExport(CurrentFileName, template, FieldLibrary.ToList());
        PreviewShared = shared ?? "(invalid)";
    }

    private void ApplyPurgeOptions(PurgeOptions options)
    {
        PurgeRvtLinks = options.PurgeRvtLinks;
        PurgeCadImports = options.PurgeCadImports;
        PurgeImages = options.PurgeImages;
        PurgePointClouds = options.PurgePointClouds;
        PurgeGroups = options.PurgeGroups;
        PurgeAssemblies = options.PurgeAssemblies;
        PurgeSpaces = options.PurgeSpaces;
        PurgeRebar = options.PurgeRebar;
        PurgeFabricReinforcement = options.PurgeFabricReinforcement;
        PurgeSheets = options.PurgeSheets;
        PurgeSchedules = options.PurgeSchedules;
        PurgeUnused = options.PurgeUnused;
    }

    private PurgeOptions BuildPurgeOptions()
    {
        return new PurgeOptions
        {
            PurgeRvtLinks = PurgeRvtLinks,
            PurgeCadImports = PurgeCadImports,
            PurgeImages = PurgeImages,
            PurgePointClouds = PurgePointClouds,
            PurgeGroups = PurgeGroups,
            PurgeAssemblies = PurgeAssemblies,
            PurgeSpaces = PurgeSpaces,
            PurgeRebar = PurgeRebar,
            PurgeFabricReinforcement = PurgeFabricReinforcement,
            PurgeSheets = PurgeSheets,
            PurgeSchedules = PurgeSchedules,
            PurgeUnused = PurgeUnused
        };
    }
}
