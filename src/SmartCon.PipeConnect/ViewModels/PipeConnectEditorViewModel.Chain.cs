using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core;


using static SmartCon.Core.Units;
namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    // ── Chain depth (+/−) ─────────────────────────────────────────────────────

    private const int MaxChainLevel = 30;

    [RelayCommand(CanExecute = nameof(CanIncrementChain))]
    private void IncrementChainDepth()
    {
        if (_chainGraph is null) return;
        int nextLevel = ChainDepth + 1;
        if (nextLevel >= _chainGraph.Levels.Count) return;

        IsBusy = true;
        StatusMessage = $"Присоединение уровня {nextLevel}…";

        try
        {
            _chainOpHandler.IncrementLevel(
                _doc, _groupSession!, _chainGraph, _snapshotStore, _warmedElementIds, nextLevel);

            ChainDepth = nextLevel;
            UpdateChainUI();
            StatusMessage = $"Уровень {nextLevel} присоединён";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Chain+] Error: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = $"Ошибка цепочки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanIncrementChain()
        => IsSessionActive && !IsBusy
        && _chainGraph is not null
        && ChainDepth < _chainGraph.MaxLevel
        && ChainDepth < MaxChainLevel;

    [RelayCommand(CanExecute = nameof(CanDecrementChain))]
    private void DecrementChainDepth()
    {
        if (_chainGraph is null || ChainDepth <= 0) return;

        IsBusy = true;
        StatusMessage = $"Откат уровня {ChainDepth}…";

        try
        {
            _chainOpHandler.DecrementLevel(
                _doc, _groupSession!, _chainGraph, _snapshotStore, ChainDepth);

            ChainDepth--;
            UpdateChainUI();
            StatusMessage = $"Уровень {ChainDepth + 1} отсоединён";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Chain−] Error: {ex.Message}");
            StatusMessage = $"Ошибка отката: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDecrementChain()
        => IsSessionActive && !IsBusy && ChainDepth > 0;

    [RelayCommand(CanExecute = nameof(CanConnectAllChain))]
    private void ConnectAllChain()
    {
        if (_chainGraph is null) return;

        IsBusy = true;
        StatusMessage = "Подключение всей сети…";

        try
        {
            int targetLevel = _chainGraph.MaxLevel;
            int processed = 0;

            while (ChainDepth < targetLevel && ChainDepth < MaxChainLevel)
            {
                IncrementChainDepth();
                processed++;
            }

            StatusMessage = $"Подключено {processed} уровней";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[ConnectAll] Error: {ex.Message}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnectAllChain()
        => IsSessionActive && !IsBusy
        && _chainGraph is not null
        && ChainDepth < _chainGraph.MaxLevel
        && ChainDepth < MaxChainLevel;

    private void UpdateChainUI()
    {
        if (_chainGraph is null || _chainGraph.TotalChainElements == 0)
        {
            HasChain = false;
        }
        else
        {
            HasChain = true;
        }

        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
        ConnectAllChainCommand.NotifyCanExecuteChanged();
    }
}
