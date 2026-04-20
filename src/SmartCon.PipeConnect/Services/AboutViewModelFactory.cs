using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Services;

public sealed class AboutViewModelFactory(
    IUpdateService updateService,
    IUpdateSettingsRepository updateSettings) : IAboutViewModelFactory
{
    public AboutViewModel Create() => new(updateService, updateSettings);
}
