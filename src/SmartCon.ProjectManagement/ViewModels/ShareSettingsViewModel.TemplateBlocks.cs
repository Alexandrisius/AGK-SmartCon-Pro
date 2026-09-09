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
    private void AddBlock()
    {
        var index = Blocks.Count > 0 ? Blocks.Max(b => b.Index) + 1 : 0;
        var item = new FileNameBlockItem
        {
            Index = index,
            Field = string.Empty,
            ParseRule = ParseRule.DefaultDelimiter("-", index)
        };
        item.PropertyChanged += OnBlockItemChanged;
        Blocks.Add(item);
        RefreshValidation();
        RefreshPreview();
    }

    [RelayCommand]
    private void RemoveBlock()
    {
        if (SelectedBlockIndex >= 0 && SelectedBlockIndex < Blocks.Count)
        {
            Blocks.RemoveAt(SelectedBlockIndex);

            for (int i = 0; i < Blocks.Count; i++)
                Blocks[i].Index = i;

            RefreshValidation();
            RefreshPreview();
        }
    }

    [RelayCommand]
    private void MoveBlockUp()
    {
        if (SelectedBlockIndex <= 0) return;

        var targetIndex = SelectedBlockIndex - 1;

        foreach (var b in Blocks) b.PropertyChanged -= OnBlockItemChanged;

        Blocks.Move(SelectedBlockIndex, targetIndex);

        for (int i = 0; i < Blocks.Count; i++)
            Blocks[i].Index = i;

        foreach (var b in Blocks) b.PropertyChanged += OnBlockItemChanged;

        AutoParseFileName();
        RefreshValidation();
        RefreshPreview();

        SelectedBlockIndex = targetIndex;
    }

    [RelayCommand]
    private void MoveBlockDown()
    {
        if (SelectedBlockIndex < 0 || SelectedBlockIndex >= Blocks.Count - 1) return;

        var targetIndex = SelectedBlockIndex + 1;

        foreach (var b in Blocks) b.PropertyChanged -= OnBlockItemChanged;

        Blocks.Move(SelectedBlockIndex, targetIndex);

        for (int i = 0; i < Blocks.Count; i++)
            Blocks[i].Index = i;

        foreach (var b in Blocks) b.PropertyChanged += OnBlockItemChanged;

        AutoParseFileName();
        RefreshValidation();
        RefreshPreview();

        SelectedBlockIndex = targetIndex;
    }

    [RelayCommand]
    private void OpenParseRuleEditor()
    {
        if (SelectedBlockIndex < 0 || SelectedBlockIndex >= Blocks.Count) return;

        var block = Blocks[SelectedBlockIndex];
        var precedingRules = Blocks
            .OrderBy(b => b.Index)
            .TakeWhile(b => b.Index != block.Index)
            .Select(b => b.ParseRule)
            .ToList();

        var originalRule = block.ParseRule;

        try
        {
            SmartConLogger.Info("OpenParseRuleEditor: creating ViewModel");
            var vm = new ParseRuleViewModel(block.ParseRule, CurrentFileName, precedingRules);

            bool? dialogResult = null;
            vm.RequestClose += result => dialogResult = result;

            SmartConLogger.Info("OpenParseRuleEditor: calling ShowDialog via presenter");
            _dialogPresenter.ShowDialog(vm);
            SmartConLogger.Info("OpenParseRuleEditor: ShowDialog returned");

            if (dialogResult != true) return;

            block.ParseRule = vm.BuildRule();
            block.RefreshParseRuleDisplay();
            AutoParseFileName();
            RefreshPreview();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenParseRuleEditor failed:\n{ex}");
            _dialogService.ShowError("Error", $"OpenParseRuleEditor error:\n\n{ex.Message}\n\n{ex.StackTrace}");
        }
    }

    [RelayCommand]
    private void AddExportMapping()
    {
        var item = new ExportMappingItem();
        item.PropertyChanged += OnMappingItemChanged;
        ExportMappings.Add(item);
        UpdateMappingCurrentValues();
    }

    [RelayCommand]
    private void RemoveExportMapping()
    {
        if (SelectedMappingIndex >= 0 && SelectedMappingIndex < ExportMappings.Count)
            ExportMappings.RemoveAt(SelectedMappingIndex);
    }

    private FileNameTemplate BuildTemplate()
    {
        return new FileNameTemplate
        {
            Blocks = Blocks.Select(b => new FileBlockDefinition
            {
                Index = b.Index,
                Field = b.Field,
                ParseRule = b.ParseRule
            }).ToList(),
            ExportMappings = ExportMappings.Select(m => new ExportMapping
            {
                Field = m.Field,
                SourceValue = m.SourceValue,
                TargetValue = m.TargetValue
            }).ToList()
        };
    }

    private ShareProjectSettings BuildSettings()
    {
        return new ShareProjectSettings
        {
            ShareFolderPath = ShareFolderPath,
            SyncBeforeShare = SyncBeforeShare,
            FieldLibrary = FieldLibrary.ToList(),
            PurgeOptions = BuildPurgeOptions(),
            KeepViewNames = Views.Where(v => v.IsSelected).Select(v => v.Name).ToList(),
            FileNameTemplate = BuildTemplate()
        };
    }

    private void LoadBlocksFromSettings(FileNameTemplate template)
    {
        Blocks.Clear();
        foreach (var b in template.Blocks)
        {
            var item = new FileNameBlockItem
            {
                Index = b.Index,
                Field = b.Field,
                ParseRule = b.ParseRule
            };
            item.PropertyChanged += OnBlockItemChanged;
            Blocks.Add(item);
        }
    }

    private void LoadExportMappingsFromSettings(FileNameTemplate template)
    {
        ExportMappings.Clear();
        foreach (var m in template.ExportMappings)
        {
            var item = new ExportMappingItem
            {
                Field = m.Field,
                SourceValue = m.SourceValue,
                TargetValue = m.TargetValue
            };
            item.PropertyChanged += OnMappingItemChanged;
            ExportMappings.Add(item);
        }
    }
}
