using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Services;

public sealed class SettingsViewModelFactory(
    IFittingMappingRepository mappingRepo,
    IFittingFamilyRepository familyRepo,
    IDialogService dialogService) : ISettingsViewModelFactory
{
    public MappingEditorViewModel Create(Document doc)
    {
        var eligibleFamilies = familyRepo.GetEligibleFittingFamilies(doc);
        var familyNames = eligibleFamilies.Select(f => f.FamilyName).ToList();
        return new MappingEditorViewModel(mappingRepo, familyNames, dialogService);
    }
}
