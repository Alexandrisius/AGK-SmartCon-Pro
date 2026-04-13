using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

/// <summary>
/// Тесты VirtualCtcStore через internal long-перегрузки (без RevitAPI).
/// </summary>
public sealed class VirtualCtcStoreTests
{
    [Fact]
    public void Get_Empty_ReturnsNull()
    {
        var store = new VirtualCtcStore();
        Assert.Null(store.Get(100L, 0));
    }

    [Fact]
    public void Set_Get_ReturnsSameCtc()
    {
        var store = new VirtualCtcStore();
        store.Set(100L, 0, new ConnectionTypeCode(3));

        var result = store.Get(100L, 0);
        Assert.NotNull(result);
        Assert.Equal(3, result.Value.Value);
    }

    [Fact]
    public void Set_DifferentConnectors_Independent()
    {
        var store = new VirtualCtcStore();
        store.Set(100L, 0, new ConnectionTypeCode(1));
        store.Set(100L, 1, new ConnectionTypeCode(2));

        Assert.Equal(1, store.Get(100L, 0)!.Value.Value);
        Assert.Equal(2, store.Get(100L, 1)!.Value.Value);
    }

    [Fact]
    public void Set_DifferentElements_Independent()
    {
        var store = new VirtualCtcStore();
        store.Set(100L, 0, new ConnectionTypeCode(5));
        store.Set(200L, 0, new ConnectionTypeCode(7));

        Assert.Equal(5, store.Get(100L, 0)!.Value.Value);
        Assert.Equal(7, store.Get(200L, 0)!.Value.Value);
    }

    [Fact]
    public void Set_Overwrite_ReturnsLatest()
    {
        var store = new VirtualCtcStore();
        store.Set(100L, 0, new ConnectionTypeCode(1));
        store.Set(100L, 0, new ConnectionTypeCode(9));

        Assert.Equal(9, store.Get(100L, 0)!.Value.Value);
    }

    [Fact]
    public void GetOverridesForElement_ReturnsAll()
    {
        var store = new VirtualCtcStore();
        store.Set(100L, 0, new ConnectionTypeCode(1));
        store.Set(100L, 1, new ConnectionTypeCode(2));
        store.Set(200L, 0, new ConnectionTypeCode(3));

        var overrides = store.GetOverridesForElementByIdValue(100L);
        Assert.Equal(2, overrides.Count);
        Assert.Equal(1, overrides[0].Value);
        Assert.Equal(2, overrides[1].Value);
    }

    [Fact]
    public void GetOverridesForElement_Empty_ReturnsEmpty()
    {
        var store = new VirtualCtcStore();
        var overrides = store.GetOverridesForElementByIdValue(999L);
        Assert.Empty(overrides);
    }

    [Fact]
    public void HasPendingWrites_NoPendingWrites_False()
    {
        var store = new VirtualCtcStore();
        store.Set(100L, 0, new ConnectionTypeCode(1));
        Assert.False(store.HasPendingWrites);
    }

    [Fact]
    public void HasPendingWrites_WithTypeDef_True()
    {
        var store = new VirtualCtcStore();
        var typeDef = new ConnectorTypeDefinition { Code = 1, Name = "Сварка", Description = "Сварное" };
        store.Set(100L, 0, new ConnectionTypeCode(1), typeDef);
        Assert.True(store.HasPendingWrites);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var store = new VirtualCtcStore();
        var td = new ConnectorTypeDefinition { Code = 1, Name = "X" };
        store.Set(100L, 0, new ConnectionTypeCode(1), td);
        store.Set(200L, 1, new ConnectionTypeCode(2));

        store.Clear();

        Assert.Null(store.Get(100L, 0));
        Assert.Null(store.Get(200L, 1));
        Assert.False(store.HasPendingWrites);
    }

    [Fact]
    public void Set_OverwriteWithTypeDef_UpdatesPending()
    {
        var store = new VirtualCtcStore();
        var td1 = new ConnectorTypeDefinition { Code = 1, Name = "Old" };
        var td2 = new ConnectorTypeDefinition { Code = 2, Name = "New" };
        store.Set(100L, 0, new ConnectionTypeCode(1), td1);
        store.Set(100L, 0, new ConnectionTypeCode(2), td2);

        Assert.Equal(2, store.Get(100L, 0)!.Value.Value);
    }
}
