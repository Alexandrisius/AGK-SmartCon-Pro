using SmartCon.Core.Models;
using SmartCon.ProjectManagement.ViewModels;
using Xunit;

namespace SmartCon.Tests.ProjectManagement;

public sealed class ParseRuleViewModelTests
{
    private static ParseRuleViewModel CreateVm(
        ParseRule? initialRule = null,
        string previewFileName = "PRJ-S0-DEV.rvt",
        List<ParseRule>? precedingRules = null)
    {
        return new ParseRuleViewModel(
            initialRule ?? ParseRule.DefaultDelimiter(),
            previewFileName,
            precedingRules ?? []);
    }

    [Fact]
    public void Constructor_InitializesFromRule()
    {
        var rule = new ParseRule
        {
            Mode = ParseMode.BetweenMarkers,
            OpenMarker = "[",
            CloseMarker = "]"
        };

        var vm = CreateVm(rule);

        Assert.Equal(ParseMode.BetweenMarkers, vm.Mode);
        Assert.Equal("[", vm.OpenMarker);
        Assert.Equal("]", vm.CloseMarker);
    }

    [Fact]
    public void BuildRule_DelimiterSegment_CreatesCorrectRule()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "_";
        vm.SegmentIndex = 3;

        var rule = vm.BuildRule();

        Assert.Equal(ParseMode.DelimiterSegment, rule.Mode);
        Assert.Equal("_", rule.Delimiter);
        Assert.Equal(3, rule.SegmentIndex);
    }

    [Fact]
    public void BuildRule_FixedWidth_CreatesCorrectRule()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.FixedWidth;
        vm.CharCount = 5;

        var rule = vm.BuildRule();

        Assert.Equal(ParseMode.FixedWidth, rule.Mode);
        Assert.Equal(5, rule.CharCount);
    }

    [Fact]
    public void BuildRule_BetweenMarkers_CreatesCorrectRule()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.BetweenMarkers;
        vm.OpenMarker = "(";
        vm.CloseMarker = ")";

        var rule = vm.BuildRule();

        Assert.Equal(ParseMode.BetweenMarkers, rule.Mode);
        Assert.Equal("(", rule.OpenMarker);
        Assert.Equal(")", rule.CloseMarker);
    }

    [Fact]
    public void BuildRule_AfterMarker_CreatesCorrectRule()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.AfterMarker;
        vm.Marker = "->";

        var rule = vm.BuildRule();

        Assert.Equal(ParseMode.AfterMarker, rule.Mode);
        Assert.Equal("->", rule.Marker);
    }

    [Fact]
    public void BuildRule_Remainder_CreatesCorrectRule()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.Remainder;

        var rule = vm.BuildRule();

        Assert.Equal(ParseMode.Remainder, rule.Mode);
    }

    [Fact]
    public void Preview_DelimiterSegment_ExtractsCorrectValue()
    {
        var vm = CreateVm(previewFileName: "PRJ-S0-DEV.rvt");
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "-";
        vm.SegmentIndex = 2;

        Assert.Equal("S0", vm.PreviewValue);
    }

    [Fact]
    public void Preview_FixedWidth_ExtractsCorrectValue()
    {
        var vm = CreateVm(previewFileName: "PRJ-S0-DEV.rvt");
        vm.Mode = ParseMode.FixedWidth;
        vm.CharCount = 3;

        Assert.Equal("PRJ", vm.PreviewValue);
    }

    [Fact]
    public void Preview_BetweenMarkers_ExtractsCorrectValue()
    {
        var vm = CreateVm(previewFileName: "PRJ(S0)DEV.rvt");
        vm.Mode = ParseMode.BetweenMarkers;
        vm.OpenMarker = "(";
        vm.CloseMarker = ")";

        Assert.Equal("S0", vm.PreviewValue);
    }

    [Fact]
    public void Preview_AfterMarker_ExtractsCorrectValue()
    {
        var vm = CreateVm(previewFileName: "PRJ-S0-DEV.rvt");
        vm.Mode = ParseMode.AfterMarker;
        vm.Marker = "-";

        Assert.Equal("S0-DEV", vm.PreviewValue);
    }

    [Fact]
    public void Preview_Remainder_ExtractsEverything()
    {
        var vm = CreateVm(previewFileName: "PRJ-S0-DEV.rvt");
        vm.Mode = ParseMode.Remainder;

        Assert.Equal("PRJ-S0-DEV", vm.PreviewValue);
    }

    [Fact]
    public void Preview_WithPrecedingRules_AppliesThemFirst()
    {
        var preceding = new List<ParseRule>
        {
            ParseRule.DefaultDelimiter("-", 1)
        };

        var vm = CreateVm(
            previewFileName: "PRJ-S0-DEV.rvt",
            precedingRules: preceding);
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "-";
        vm.SegmentIndex = 2;

        Assert.Equal("DEV", vm.PreviewValue);
    }

    [Fact]
    public void Preview_Remaining_ReflectsLeftoverText()
    {
        var vm = CreateVm(previewFileName: "PRJ-S0-DEV.rvt");
        vm.Mode = ParseMode.FixedWidth;
        vm.CharCount = 3;

        Assert.Equal("-S0-DEV", vm.PreviewRemaining);
    }

    [Fact]
    public void Preview_EmptyFileName_ReturnsEmpty()
    {
        var vm = CreateVm(previewFileName: "");

        Assert.Equal(string.Empty, vm.PreviewValue);
        Assert.Equal(string.Empty, vm.PreviewRemaining);
    }

    [Fact]
    public void ModeVisibility_FlagsMatchCurrentMode()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.FixedWidth;

        Assert.True(vm.IsFixedWidth);
        Assert.False(vm.IsDelimiterSegment);
        Assert.False(vm.IsBetweenMarkers);
        Assert.False(vm.IsAfterMarker);
        Assert.False(vm.IsRemainder);
    }

    [Fact]
    public void OkCommand_InvokesRequestCloseWithTrue()
    {
        var vm = CreateVm();
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.OkCommand.Execute(null);

        Assert.True(result);
    }

    [Fact]
    public void CancelCommand_InvokesRequestCloseWithFalse()
    {
        var vm = CreateVm();
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.CancelCommand.Execute(null);

        Assert.False(result);
    }

    [Fact]
    public void Constructor_InitializesSegmentCountFromRule()
    {
        var rule = new ParseRule
        {
            Mode = ParseMode.DelimiterSegment,
            Delimiter = "-",
            SegmentIndex = 1,
            SegmentCount = 2
        };

        var vm = CreateVm(rule);

        Assert.Equal(2, vm.SegmentCount);
    }

    [Fact]
    public void BuildRule_DelimiterSegment_WithSegmentCount()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "-";
        vm.SegmentIndex = 1;
        vm.SegmentCount = 2;

        var rule = vm.BuildRule();

        Assert.Equal(1, rule.SegmentIndex);
        Assert.Equal(2, rule.SegmentCount);
    }

    [Fact]
    public void Preview_MultiSegment_ExtractsCorrectValue()
    {
        var vm = CreateVm(previewFileName: "12-59-Сарай.rvt");
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "-";
        vm.SegmentIndex = 1;
        vm.SegmentCount = 2;

        Assert.Equal("12-59", vm.PreviewValue);
        Assert.Equal("Сарай", vm.PreviewRemaining);
    }

    [Fact]
    public void Preview_SegmentCountChanged_RefreshesPreview()
    {
        var vm = CreateVm(previewFileName: "A-B-C-D.rvt");
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "-";
        vm.SegmentIndex = 2;
        vm.SegmentCount = 1;

        Assert.Equal("B", vm.PreviewValue);

        vm.SegmentCount = 2;

        Assert.Equal("B-C", vm.PreviewValue);
        Assert.Equal("A-D", vm.PreviewRemaining);
    }
}
