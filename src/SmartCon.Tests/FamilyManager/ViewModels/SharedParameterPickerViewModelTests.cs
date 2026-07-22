using System.IO;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class SharedParameterPickerViewModelTests
{
    private readonly Mock<ISharedParameterFileParser> _parserMock = new();
    private readonly Mock<IFamilyManagerUserSettingsRepository> _settingsMock = new();
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();

    private static readonly SharedParameterEntry[] s_entries =
    [
        new(Guid.NewGuid(), "ADSK_Масса", "NUMBER", null, "01 Общие", "Масса единицы"),
        new(Guid.NewGuid(), "ADSK_Длина", "LENGTH", null, "10 Размеры", null),
        new(Guid.NewGuid(), "ADSK_Примечание", "TEXT", null, null, null),
    ];

    public SharedParameterPickerViewModelTests()
    {
        _parserMock.Setup(p => p.ParseFile(It.IsAny<string>())).Returns(s_entries);
        _settingsMock.Setup(s => s.Load()).Returns(FamilyManagerUserSettings.Default);
    }

    private SharedParameterPickerViewModel CreateVm(params string[] existingNames)
        => new(_parserMock.Object, _settingsMock.Object, _dialogMock.Object, existingNames);

    private string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        File.WriteAllText(path, "dummy");
        return path;
    }

    private void BrowseAndLoad(SharedParameterPickerViewModel vm, string path)
    {
        _dialogMock.Setup(d => d.ShowOpenTextFileDialog(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(path);
        vm.BrowseCommand.Execute(null);
    }

    [Fact]
    public void Initialize_NoCachedPath_StaysEmpty()
    {
        var vm = CreateVm();

        vm.Initialize();

        Assert.False(vm.IsLoaded);
        Assert.Empty(vm.Items);
        Assert.Empty(vm.FilePath);
    }

    [Fact]
    public void Initialize_CachedPathExists_AutoLoads()
    {
        var path = CreateTempFile();
        try
        {
            _settingsMock.Setup(s => s.Load())
                .Returns(new FamilyManagerUserSettings(path));

            var vm = CreateVm();
            vm.Initialize();

            Assert.True(vm.IsLoaded);
            Assert.Equal(3, vm.Items.Count);
            Assert.Equal(path, vm.FilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Initialize_CachedPathMissing_ShowsWarningNoLoad()
    {
        _settingsMock.Setup(s => s.Load())
            .Returns(new FamilyManagerUserSettings(Path.Combine(Path.GetTempPath(), "missing-sp-file.txt")));

        var vm = CreateVm();
        vm.Initialize();

        Assert.False(vm.IsLoaded);
        Assert.Empty(vm.Items);
        Assert.NotEmpty(vm.StatusMessage);
    }

    [Fact]
    public void Browse_FilePicked_LoadsAndCachesPath()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm();
            BrowseAndLoad(vm, path);

            Assert.True(vm.IsLoaded);
            Assert.Equal(3, vm.TotalCount);
            _settingsMock.Verify(
                s => s.Save(It.Is<FamilyManagerUserSettings>(x => x.SharedParametersFilePath == path)),
                Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Browse_Cancelled_NothingChanges()
    {
        _dialogMock.Setup(d => d.ShowOpenTextFileDialog(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string?)null);

        var vm = CreateVm();
        vm.BrowseCommand.Execute(null);

        Assert.False(vm.IsLoaded);
        _settingsMock.Verify(s => s.Save(It.IsAny<FamilyManagerUserSettings>()), Times.Never);
    }

    [Fact]
    public void Load_ExistingNames_MarkedAlreadyExistsAndNotSelectable()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm("ADSK_Масса");
            BrowseAndLoad(vm, path);

            var existing = Assert.Single(vm.Items, i => i.Name == "ADSK_Масса");
            Assert.True(existing.AlreadyExists);
            Assert.False(existing.CanSelect);

            var fresh = Assert.Single(vm.Items, i => i.Name == "ADSK_Длина");
            Assert.False(fresh.AlreadyExists);
            Assert.True(fresh.CanSelect);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SearchText_FiltersByNameGroupDescription()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm();
            BrowseAndLoad(vm, path);

            vm.SearchText = "размеры";
            Assert.Single(vm.Items);

            vm.SearchText = "масса единицы";
            Assert.Single(vm.Items);

            vm.SearchText = "ADSK_";
            Assert.Equal(3, vm.Items.Count);

            vm.SearchText = string.Empty;
            Assert.Equal(3, vm.Items.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SelectAll_SelectsOnlySelectableVisibleItems()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm("ADSK_Масса");
            BrowseAndLoad(vm, path);

            vm.SelectAllCommand.Execute(null);

            Assert.Equal(2, vm.SelectedCount);
            Assert.False(vm.Items.First(i => i.AlreadyExists).IsSelected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeselectAll_ClearsSelection()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm();
            BrowseAndLoad(vm, path);
            vm.SelectAllCommand.Execute(null);
            Assert.Equal(3, vm.SelectedCount);

            vm.DeselectAllCommand.Execute(null);

            Assert.Equal(0, vm.SelectedCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ok_NoSelection_ShowsStatusAndStaysOpen()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm();
            BrowseAndLoad(vm, path);

            bool? closed = null;
            vm.RequestClose += r => closed = r;

            vm.OkCommand.Execute(null);

            Assert.Null(closed);
            Assert.NotEmpty(vm.StatusMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ok_WithSelection_ClosesTrueAndReturnsEntries()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm("ADSK_Масса");
            BrowseAndLoad(vm, path);

            vm.SelectAllCommand.Execute(null);

            bool? closed = null;
            vm.RequestClose += r => closed = r;

            vm.OkCommand.Execute(null);

            Assert.True(closed);
            var selected = vm.GetSelectedEntries();
            Assert.Equal(2, selected.Count);
            Assert.DoesNotContain(selected, e => e.Name == "ADSK_Масса");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ParseFailure_ShowsErrorStatus()
    {
        var path = CreateTempFile();
        try
        {
            _parserMock.Setup(p => p.ParseFile(It.IsAny<string>()))
                .Throws(new InvalidDataException("bad file"));

            var vm = CreateVm();
            BrowseAndLoad(vm, path);

            Assert.False(vm.IsLoaded);
            Assert.Empty(vm.Items);
            Assert.NotEmpty(vm.StatusMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GroupFilter_PopulatedFromFile_SortedWithAllGroupsFirst()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm();
            BrowseAndLoad(vm, path);

            Assert.Equal(3, vm.AvailableFopGroups.Count);
            Assert.Equal("Все группы", vm.AvailableFopGroups[0]);
            Assert.Equal("01 Общие", vm.AvailableFopGroups[1]);
            Assert.Equal("10 Размеры", vm.AvailableFopGroups[2]);
            Assert.Equal("Все группы", vm.SelectedFopGroup);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GroupFilter_SelectedGroup_FiltersItems()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm();
            BrowseAndLoad(vm, path);

            vm.SelectedFopGroup = "10 Размеры";

            var item = Assert.Single(vm.Items);
            Assert.Equal("ADSK_Длина", item.Name);

            vm.SelectedFopGroup = "Все группы";
            Assert.Equal(3, vm.Items.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GroupFilter_CombinesWithSearchText()
    {
        var path = CreateTempFile();
        try
        {
            var vm = CreateVm();
            BrowseAndLoad(vm, path);

            vm.SelectedFopGroup = "01 Общие";
            vm.SearchText = "масса";

            var item = Assert.Single(vm.Items);
            Assert.Equal("ADSK_Масса", item.Name);

            vm.SearchText = "длина";
            Assert.Empty(vm.Items);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
