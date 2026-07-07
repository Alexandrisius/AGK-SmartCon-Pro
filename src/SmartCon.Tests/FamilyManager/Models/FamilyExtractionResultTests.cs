using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// Unit tests for <see cref="FamilyExtractionResult.SharedNestedFamilyNames"/>
/// and the non-null <see cref="FamilyExtractionResult.SharedNestedFamilyNamesSafe"/>
/// accessor added in V3 (ADR-034 §2).
///
/// The field is nullable with default <c>null</c> for source compatibility with
/// legacy callers that pre-date the shared-nested fallback feature. Production
/// paths (<see cref="RevitFamilyDataExtractionService.ExtractFromManagedFile"/>
/// and the <see cref="SmartCon.FamilyManager.Services.LocalCatalog.LocalSharedNestedFamilyRepository"/>
/// SaveSharedNestedNames call site) always populate the field via
/// <c>result with { SharedNestedFamilyNames = sharedNames }</c> or the
/// 6-arg constructor. The <see cref="SharedNestedFamilyNamesSafe"/> accessor
/// guarantees consumers never have to null-check.
/// </summary>
public sealed class FamilyExtractionResultTests
{
    [Fact]
    public void SharedNestedFamilyNamesSafe_WhenFieldIsNull_ReturnsEmptyArray()
    {
        var result = new FamilyExtractionResult(
            Success: true,
            Types: [],
            UntypedValues: null,
            ErrorMessage: null,
            RevitMajorVersion: 2025);

        Assert.Null(result.SharedNestedFamilyNames);
        Assert.NotNull(result.SharedNestedFamilyNamesSafe);
        Assert.Empty(result.SharedNestedFamilyNamesSafe);
    }

    [Fact]
    public void SharedNestedFamilyNamesSafe_WhenFieldIsNull_ReturnsStableEmptyInstance()
    {
        // Two reads return the SAME empty array instance — Array.Empty<string>()
        // is a singleton, so consumers can rely on reference equality if they cache it.
        var result = new FamilyExtractionResult(true, [], null, null, 2025);

        var first = result.SharedNestedFamilyNamesSafe;
        var second = result.SharedNestedFamilyNamesSafe;

        Assert.Same(first, second);
    }

    [Fact]
    public void SharedNestedFamilyNamesSafe_WhenFieldIsSet_ReturnsSameInstance()
    {
        // The accessor MUST return the actual stored instance, not a copy,
        // so a consumer that reuses the returned reference (e.g. for a check +
        // pass-through to the repository) does not pay a defensive-copy cost.
        var names = new[] { "Болт М12", "Гайка М12" };
        var result = new FamilyExtractionResult(
            Success: true,
            Types: [],
            UntypedValues: null,
            ErrorMessage: null,
            RevitMajorVersion: 2025,
            SharedNestedFamilyNames: names);

        Assert.Same(names, result.SharedNestedFamilyNamesSafe);
    }

    [Fact]
    public void With_SharedNestedFamilyNames_ClonesAllOtherFields()
    {
        // ADR-034 §2: production code does `result = result with { SharedNestedFamilyNames = sharedNames }`
        // to populate the field AFTER the original record was constructed
        // (in ExtractCore). The `with` clone must preserve every other field
        // and not regenerate the record (no new types, no lost untyped values).
        var original = new FamilyExtractionResult(
            Success: true,
            Types: [new FamilyExtractionTypeValues("TypeA", 0, [])],
            UntypedValues: [new FamilyExtractionValueResult(
                "P1", AttributeScope.Type, "String", "v", null, null, null,
                AttributeValueStatus.Found, null)],
            ErrorMessage: null,
            RevitMajorVersion: 2025);

        var shared = new[] { "Болт М12" };
        var cloned = original with { SharedNestedFamilyNames = shared };

        Assert.True(cloned.Success);
        Assert.Single(cloned.Types);
        Assert.Equal("TypeA", cloned.Types[0].TypeName);
        Assert.NotNull(cloned.UntypedValues);
        Assert.Single(cloned.UntypedValues);
        Assert.Equal("P1", cloned.UntypedValues[0].ParameterName);
        Assert.Null(cloned.ErrorMessage);
        Assert.Equal(2025, cloned.RevitMajorVersion);
        Assert.Same(shared, cloned.SharedNestedFamilyNames);
        Assert.Same(shared, cloned.SharedNestedFamilyNamesSafe);
    }

    [Fact]
    public void Constructor_Legacy5ArgCall_DoesNotSetSharedNestedFamilyNames()
    {
        // Source-compat: existing call sites (FamilyDataImportServiceTests fixtures
        // and SystemFamilyAttributeExtractionService) construct the record with
        // 5 positional args. They must continue to compile and the field must
        // default to null. The accessor returns the empty array, which
        // SaveSharedNestedNamesAsync treats as a no-op (count == 0).
        var result = new FamilyExtractionResult(
            true,
            [new FamilyExtractionTypeValues("TypeA", 0, [])],
            null,
            null,
            2025);

        Assert.Null(result.SharedNestedFamilyNames);
        Assert.Empty(result.SharedNestedFamilyNamesSafe);
    }
}
