using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.Core;

public sealed class FamilyHealthReportTests
{
    [Fact]
    public void Healthy_Singleton_NoIssues()
    {
        Assert.True(FamilyHealthReport.Healthy.IsHealthy);
        Assert.Empty(FamilyHealthReport.Healthy.Issues);
    }

    [Fact]
    public void FromIssues_OnlyWarnings_IsHealthy()
    {
        var report = FamilyHealthReport.FromIssues(
        [
            new FamilyHealthIssue("TypeA", FamilyHealthIssueSeverity.Warning, "slightly off axis"),
            new FamilyHealthIssue(null, FamilyHealthIssueSeverity.Warning, "duplicate mark"),
        ]);

        Assert.True(report.IsHealthy);
        Assert.Equal(2, report.Issues.Count);
    }

    [Fact]
    public void FromIssues_ContainsError_NotHealthy()
    {
        var report = FamilyHealthReport.FromIssues(
        [
            new FamilyHealthIssue("TypeA", FamilyHealthIssueSeverity.Warning, "warn"),
            new FamilyHealthIssue("TypeB", FamilyHealthIssueSeverity.Error, "formula error"),
        ]);

        Assert.False(report.IsHealthy);
    }
}
