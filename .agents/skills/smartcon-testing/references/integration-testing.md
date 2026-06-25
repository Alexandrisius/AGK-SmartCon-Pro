# Integration Testing — Testing Inside a Running Revit

## When you need integration tests

Use integration tests when:
- The SUT directly uses `Document`, `ElementId`, `FilteredElementCollector`, or
  other sealed Revit types that can't be wrapped in interfaces
- You need to verify behavior that depends on Revit's actual data model
  (e.g. `FamilyInstance` placement, `Connector` topology)
- You're testing the "boundary" between your abstraction and the Revit API

Don't use integration tests for:
- Pure logic (use unit tests, faster)
- WPF ViewModels (use unit tests with Moq for IExternalEventService)
- SQL queries (use in-memory SQLite via `TempCatalogFixture`)

## Three options compared (verified 2026-06-19)

| Framework | Test runner | Revit versions | Auto-install | CI/CD | Notes |
|---|---|---|---|---|---|
| **ricaun.RevitTest** | NUnit | 2019-2025 + Preview | ✅ | ✅ (incl. Design Automation) | **Recommended** for multi-version + CI |
| **RevitXunit.TestAdapter** | xUnit | 2025+ (net8.0) | ✅ (named pipes) | ✅ | Best for xUnit consistency |
| **Nice3point RevitUnit** | TUnit | 2025-2026 (net8.0) | ✅ (source-gen) | ✅ | Newest, but small community |
| **Speckle xUnitRevit** | xUnit | 2021-2024 (net48) | ❌ (manual) | ❌ | Best legacy pattern |
| **DynamoDS RevitTestFramework** | NUnit | abandoned | — | — | ❌ Last commit 6+ years ago |

**Recommendation for SmartCon:** ricaun.RevitTest (NUnit) for CI + multi-version
support, **OR** RevitXunit.TestAdapter (xUnit) for consistency with existing
unit tests.

## Option A: ricaun.RevitTest (NUnit, multi-version, CI-ready)

Source: <https://github.com/ricaun-io/RevitTest>

```xml
<!-- New csproj: src/SmartCon.IntegrationTests/SmartCon.IntegrationTests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <AssemblyName>SmartCon.IntegrationTests</AssemblyName>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ricaun.RevitTest" Version="..." />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartCon.Core\SmartCon.Core.csproj" />
  </ItemGroup>
</Project>
```

```csharp
using NUnit.Framework;
using RevitTest;

[TestFixture]
public class StaleFamilyUpdaterIntegrationTests
{
    [Test]
    public void UpdateFamily_LoadsAndWritesMarker()
    {
        var doc = RevitTestContext.Current.Document;
        var family = LoadSampleFamily(doc);
        var sut = new StaleFamilyUpdater(/* injected dependencies */);
        var result = sut.UpdateFamilyAsync("cat-1", false, default).Result;
        Assert.IsTrue(result);
    }
}
```

Key features:
- Auto-installs itself as a Revit add-in
- Runs NUnit tests inside Revit process
- Supports multi-version 2019-2025 via separate test runs
- `RevitTestContext.Current.Document` gives live `Document`

## Option B: RevitXunit.TestAdapter (xUnit)

Source: <https://github.com/kristoffer-tungland/RevitTestRunner> (June 2025)

```csharp
using Xunit;
using RevitXunit;

public class StaleDetectorIntegrationTests
{
    [RevitFact(@"C:\Models\sample.rvt")]
    public async Task CheckFamily_ReturnsExpectedResult(Document doc, UIApplication app)
    {
        var detector = new StaleDetector(/* ... */);
        var result = await detector.CheckFamilyAsync("cat-1", "Family-1", doc, /* id */, default);
        Assert.True(result.IsStale);
    }
}
```

## Option C: Mock RevitAPI (limited, no full integration)

For **simulating** the Revit API surface without running Revit, the only
realistic option is to write thin adapter classes that wrap Revit types and
test the adapter via the interface. This is NOT a substitute for integration
testing — it shifts the boundary but doesn't actually exercise the Revit
engine.

## Production log validation (SmartCon pattern)

When integration tests are not feasible, **validate via production logs**:

1. Add structured logging via `SmartConLogger.BeginScope("StaleDetection", ...)`
   in the SUT
2. Run the SUT manually in Revit 2024 and/or 2025
3. Inspect `%APPDATA%\AGK\SmartCon\smartcon.log` for:
   - `OpId` correlation chain (all log lines from one operation share the
     same `OpId`)
   - `[DBG]` lines that confirm internal state transitions
   - `[INF]` lines that confirm successful operations
   - `[WRN]` lines that include `[Action: ...]` suggestions (L9 compliance)
   - Absence of `[ERR]` lines

Example validation (2026-06-19):
```
[INF] [OpId=... Op=StaleDetection Method=CheckCategoryAsync CategoryId=...] CheckCategory: merged 1 results (1 stale). Snapshot size: 0 -> 1.
[INF] [OpId=... Op=StaleDetection Method=UpdateCategoryStaleAsync CategoryId=...] Batch update: 1/1 succeeded. Failed: []
[INF] [OpId=... Op=StaleDetection Method=UpdateCategoryStaleAsync CategoryId=...] MarkUpdated: removed 1 entries. Snapshot size: 42.
```

See `docs/testing/stale-detection-coverage-gaps.md` for the real-world example
of what was validated by production logs vs unit tests.

## CI/CD integration

For ricaun.RevitTest in CI:
- GitHub Actions matrix: `{ revit: [2023, 2024, 2025] }`
- Windows runner (Revit is Windows-only)
- Each matrix job installs Revit + runs tests
- Design Automation for Revit (Forge) for cloud-based testing

For RevitXunit.TestAdapter in CI:
- Similar matrix, but with xUnit runner
- Named-pipe communication between test runner and Revit process

## When to write integration tests (decision flow)

```
Is the SUT pure logic (no Revit types)?
  YES → unit test
  NO  → can we wrap the Revit type in an interface and inject it?
    YES → unit test with Moq on the wrapper interface
    NO  → integration test (ricaun.RevitTest or RevitXunit.TestAdapter)
```
