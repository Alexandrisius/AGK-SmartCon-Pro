using System.IO;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core.Services;

/// <summary>
/// Тесты FittingMapper: поиск правил, зеркальные правила, кратчайший путь.
/// </summary>
public sealed class FittingMapperTests : IDisposable
{
    private readonly string _tempFile;
    private readonly JsonFittingMappingRepository _repository;

    public FittingMapperTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"smartcon-mapper-{Guid.NewGuid()}.json");
        _repository = new JsonFittingMappingRepository(_tempFile);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    private FittingMapper CreateMapper(params FittingMappingRule[] rules)
    {
        _repository.SaveMappingRules(rules.ToList());
        return new FittingMapper(_repository);
    }

    // ── GetMappings ─────────────────────────────────────────────────────

    [Fact]
    public void GetMappings_ExactMatch_ReturnsRule()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Переход", SymbolName = "*", Priority = 1 },
                ],
            });

        var result = mapper.GetMappings(new ConnectionTypeCode(1), new ConnectionTypeCode(2));

        Assert.Single(result);
        Assert.Equal(1, result[0].FromType.Value);
        Assert.Equal(2, result[0].ToType.Value);
    }

    [Fact]
    public void GetMappings_ReversedOrder_ReturnsRule()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Переход", SymbolName = "*", Priority = 1 },
                ],
            });

        var result = mapper.GetMappings(new ConnectionTypeCode(2), new ConnectionTypeCode(1));

        Assert.Single(result);
    }

    [Fact]
    public void GetMappings_SameType_NoDuplicate()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Муфта", SymbolName = "*", Priority = 1 },
                ],
            });

        var result = mapper.GetMappings(new ConnectionTypeCode(1), new ConnectionTypeCode(1));

        Assert.Single(result);
    }

    [Fact]
    public void GetMappings_NoMatch_ReturnsEmpty()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies = [],
            });

        var result = mapper.GetMappings(new ConnectionTypeCode(3), new ConnectionTypeCode(4));

        Assert.Empty(result);
    }

    [Fact]
    public void GetMappings_OrderByPriority()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Муфта2", SymbolName = "*", Priority = 5 },
                ],
            },
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Муфта1", SymbolName = "*", Priority = 1 },
                ],
            });

        var result = mapper.GetMappings(new ConnectionTypeCode(1), new ConnectionTypeCode(1));

        Assert.Equal(2, result.Count);
        Assert.Equal("Муфта1", result[0].FittingFamilies[0].FamilyName);
        Assert.Equal("Муфта2", result[1].FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void GetMappings_EmptyFamiliesGoesLast()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = false,
                FittingFamilies = [],
            },
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Муфта", SymbolName = "*", Priority = 10 },
                ],
            });

        var result = mapper.GetMappings(new ConnectionTypeCode(1), new ConnectionTypeCode(1));

        Assert.Equal(2, result.Count);
        Assert.Equal("Муфта", result[0].FittingFamilies[0].FamilyName);
        Assert.Empty(result[1].FittingFamilies);
    }

    // ── ReducerFamilies ─────────────────────────────────────────────────

    [Fact]
    public void GetMappings_ReducerFamiliesPreserved()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Муфта", SymbolName = "*", Priority = 1 },
                ],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Переход", SymbolName = "DN50-DN25", Priority = 1 },
                    new FittingMapping { FamilyName = "Ниппель", SymbolName = "*", Priority = 2 },
                ],
            });

        var result = mapper.GetMappings(new ConnectionTypeCode(1), new ConnectionTypeCode(1));

        Assert.Single(result);
        Assert.Equal(2, result[0].ReducerFamilies.Count);
        Assert.Equal("Переход", result[0].ReducerFamilies[0].FamilyName);
        Assert.Equal("Ниппель", result[0].ReducerFamilies[1].FamilyName);
    }

    [Fact]
    public void SaveAndLoad_ReducerFamiliesRoundTrip()
    {
        var rules = new List<FittingMappingRule>
        {
            new()
            {
                FromType = new ConnectionTypeCode(2),
                ToType = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Кран", SymbolName = "*", Priority = 1 },
                ],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Переходник", SymbolName = "DN32-DN25", Priority = 1 },
                ],
            },
        };

        _repository.SaveMappingRules(rules);
        var loaded = _repository.GetMappingRules();

        Assert.Single(loaded);
        Assert.Single(loaded[0].ReducerFamilies);
        Assert.Equal("Переходник", loaded[0].ReducerFamilies[0].FamilyName);
        Assert.Equal("DN32-DN25", loaded[0].ReducerFamilies[0].SymbolName);
        Assert.Equal(1, loaded[0].ReducerFamilies[0].Priority);
    }

    [Fact]
    public void SaveAndLoad_EmptyReducerFamiliesRoundTrip()
    {
        var rules = new List<FittingMappingRule>
        {
            new()
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies = [],
            },
        };

        _repository.SaveMappingRules(rules);
        var loaded = _repository.GetMappingRules();

        Assert.Single(loaded);
        Assert.Empty(loaded[0].ReducerFamilies);
    }

    // ── FindShortestFittingPath ─────────────────────────────────────────

    [Fact]
    public void FindShortestFittingPath_DirectExists_ReturnsDirect()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(3),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "1→3", SymbolName = "*", Priority = 1 },
                ],
            });

        var result = mapper.FindShortestFittingPath(new ConnectionTypeCode(1), new ConnectionTypeCode(3));

        Assert.Single(result);
    }

    [Fact]
    public void FindShortestFittingPath_Indirect_ReturnsPath()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "1→2", SymbolName = "*", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(2),
                ToType = new ConnectionTypeCode(3),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "2→3", SymbolName = "*", Priority = 1 },
                ],
            });

        var result = mapper.FindShortestFittingPath(new ConnectionTypeCode(1), new ConnectionTypeCode(3));

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].FromType.Value);
        Assert.Equal(2, result[0].ToType.Value);
        Assert.Equal(2, result[1].FromType.Value);
        Assert.Equal(3, result[1].ToType.Value);
    }

    [Fact]
    public void FindShortestFittingPath_NoPath_ReturnsEmpty()
    {
        var mapper = CreateMapper(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies = [],
            });

        var result = mapper.FindShortestFittingPath(new ConnectionTypeCode(1), new ConnectionTypeCode(5));

        Assert.Empty(result);
    }
}
