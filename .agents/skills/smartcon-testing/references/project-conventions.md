# Project Conventions — SmartCon Test-Specific Build Setup

## Test project: `src/SmartCon.Tests/SmartCon.Tests.csproj`

```xml
<TargetFrameworks>net8.0-windows</TargetFrameworks>
<UseWPF>true</UseWPF>
<EnableWindowsTargeting>true</EnableWindowsTargeting>
```

**Why `net8.0-windows` only:** R24/R21/R19 are .NET Framework 4.x and don't run
unit tests (the test project uses xUnit + WPF which requires net8+). Unit tests
are built and run only against R25's API surface, then validated in R24/R21/R19
by manual testing.

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" />
<PackageReference Include="xunit" />
<PackageReference Include="xunit.runner.visualstudio" />
<PackageReference Include="Moq" />
<PackageReference Include="coverlet.collector" />

<PackageReference Include="Nice3point.Revit.Api.RevitAPI">
  <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
<PackageReference Include="Nice3point.Revit.Api.RevitAPIUI">
  <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```

**`ExcludeAssets=runtime` is critical** — without it, the test project would
try to load native `RevitAPI.dll` and fail. See [revit-mocking.md](revit-mocking.md)
for why this matters.

## Custom Configuration: `Debug.*` does NOT define `DEBUG;TRACE`

`Microsoft.NET.Sdk` auto-defines `DEBUG;TRACE` only for the **base**
`Configuration=Debug`. Our multi-version build uses named configurations
`Debug.R19` / `Debug.R21` / `Debug.R24` / `Debug.R25` / `Debug.R26`.

**Symptom if you forget this fix:**
- `SmartConLogger.Debug(...)` calls are silently dropped at runtime
- `[DBG]` lines never appear in `smartcon.log`
- `Optimize=true` by default → dead code elimination, no `.pdb`
- `[Conditional("DEBUG")]` methods pruned (e.g. `LogScope` no-op paths)

**Fix** in `src/Directory.Build.props`:

```xml
<PropertyGroup Condition="$(Configuration.StartsWith('Debug'))">
  <DefineConstants>$(DefineConstants);DEBUG;TRACE</DefineConstants>
  <DebugSymbols>true</DebugSymbols>
  <DebugType>portable</DebugType>
  <Optimize>false</Optimize>
</PropertyGroup>
```

**Verify after adding a new `Debug.Rxx` configuration:**

```powershell
$bytes = [System.IO.File]::ReadAllBytes("$env:APPDATA\SmartCon\2025\SmartCon.Core.dll")
([System.Text.Encoding]::ASCII.GetString($bytes)).IndexOf("Init failed: ") -ne -1
# True = DEBUG branch compiled in, False = still Release
```

See `docs/adr/026-logging-migration.md` and `smartcon-logging` skill for
the full discussion.

## Multi-version: `ArgumentNullException.ThrowIfNull` for net48

Production code needs to compile under both `net8.0-windows` and `net48`.
`ArgumentNullException.ThrowIfNull` exists only in net8+.

**Pattern in production code:**

```csharp
#if NET8_0_OR_GREATER
    ArgumentNullException.ThrowIfNull(dep);
#else
    if (dep is null) throw new ArgumentNullException(nameof(dep));
#endif
```

**Do NOT** use `[NotNull]` attribute or `throw new ArgumentNullException(...)`
unconditionally — both work in tests (net8 only) but fail in net48 production
build.

## Test file structure

Every test file follows this layout:

```csharp
using <ProductionNamespace>;
using <InterfacesNamespace>;
using <Project fakes>;
using Xunit;

namespace <Mirror of production folder>;

public class <ProductionClass>Tests
{
    // 1. Helper static methods for creating test data
    private static MyResult MakeResult(...) { ... }
    private static MyInput MakeInput(...) { ... }

    // 2. Build() helper that creates SUT with all mocks
    private static (Sut, Mock<IDep1>, Mock<IDep2>, ...) Build() { ... }

    // 3. [Fact] / [Theory] tests
    [Fact]
    public void Method_State_ExpectedBehavior() { ... }
}
```

## Test method naming

`{MethodUnderTest}_{StateUnderTest}_{ExpectedBehavior}`:
- `Constructor_NullStore_Throws`
- `MergeInto_OtherCategories_ArePreserved`
- `CheckFamilyAsync_VersionLabelDiffers_ResultIsVersionMismatch`
- `BuildWhereClause_IncludeUncategorizedAndCategoryIds_BothConditionsPresent`

## What to commit vs what not

**Commit:**
- New test files in `src/SmartCon.Tests/`
- New fakes in `src/SmartCon.Tests/TestDoubles/`
- Modifications to existing test files (add tests, refactor)
- `docs/testing/coverage-gaps.md` updates

**Do NOT commit:**
- `TestResults/` (coverage output, gitignored)
- `bin/`, `obj/` (build output, gitignored)
- Comments-only changes to test files (add value, don't pad)

## Commit message style

```
test(<module>): <verb> <noun>

<summary of what was added/changed>

<bullet list of specific changes if non-trivial>

<verification: tests pass count, build config>
```

Examples from project history:
- `test(stale-detection): add 27 unit tests for snapshot POCO, SQL builder, coverage gaps doc`
- `test: add coverage for StaleSnapshotLogic edge cases`

## Coverage measurement

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 \
    --collect:"XPlat Code Coverage" \
    --results-directory ./TestResults
```

Output: `TestResults/<guid>/coverage.cobertura.xml` — line rate and branch
rate. See `docs/testing/coverage-baseline-2026-06.md` for project baseline
(38.51% line rate as of 2026-06-09).

**Coverage rule for new code:** new code should have ≥80% coverage before
merge. Exceptions: integration-only code (see
[integration-testing.md](integration-testing.md)) and sealed Revit types.
