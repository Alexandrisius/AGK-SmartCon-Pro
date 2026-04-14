using SmartCon.Core.Models;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class ConnectorTypeItemTests
{
    [Fact]
    public void From_CopiesAllFields()
    {
        var def = new ConnectorTypeDefinition { Code = 5, Name = "Фланец", Description = "Фланцевое" };
        var item = ConnectorTypeItem.From(def);

        Assert.Equal(5, item.Code);
        Assert.Equal("Фланец", item.Name);
        Assert.Equal("Фланцевое", item.Description);
    }

    [Fact]
    public void ToDefinition_RoundTrip()
    {
        var def = new ConnectorTypeDefinition { Code = 3, Name = "Раструб", Description = "Раструбное" };
        var item = ConnectorTypeItem.From(def);
        var back = item.ToDefinition();

        Assert.Equal(def.Code, back.Code);
        Assert.Equal(def.Name, back.Name);
        Assert.Equal(def.Description, back.Description);
    }

    [Fact]
    public void ToDefinition_AfterMutation_ReflectsChange()
    {
        var item = ConnectorTypeItem.From(new ConnectorTypeDefinition { Code = 1, Name = "Старое" });
        item.Name = "Новое";
        Assert.Equal("Новое", item.ToDefinition().Name);
    }

    [Fact]
    public void From_EmptyStrings_NoException()
    {
        var item = ConnectorTypeItem.From(new ConnectorTypeDefinition { Code = 0 });
        Assert.Equal(string.Empty, item.Name);
        Assert.Equal(string.Empty, item.Description);
    }

    [Fact]
    public void PropertyChanged_RaisedOnCodeChange()
    {
        var item = ConnectorTypeItem.From(new ConnectorTypeDefinition { Code = 1 });
        var changed = false;
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.Code)) changed = true; };
        item.Code = 99;
        Assert.True(changed);
    }
}
