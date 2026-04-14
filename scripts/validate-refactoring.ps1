# Regression audit script for SmartCon PipeConnect refactoring
# Runs 9 automated checks to verify no regressions after refactoring
# Usage: ./scripts/validate-refactoring.ps1 [-SkipBuild] [-SkipTests] [-Verbose]

param(
    [string]$SolutionPath = "src/SmartCon.sln",
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$Verbose
)

$ErrorActionPreference = "Continue"
$rootDir = Split-Path -Parent $PSScriptRoot
Set-Location $rootDir

$results = @{}

function Write-Check {
    param([string]$Name, [bool]$Pass, [string]$Detail = "")
    $icon = if ($Pass) { "[PASS]" } else { "[FAIL]" }
    $color = if ($Pass) { "Green" } else { "Red" }
    Write-Host "  $icon " -ForegroundColor $color -NoNewline
    Write-Host $Name
    if ($Detail) {
        Write-Host "         $Detail" -ForegroundColor DarkGray
    }
    $script:results[$Name] = @{ Pass = $Pass; Detail = $Detail }
}

function Count-Lines {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    return (Get-Content $Path | Measure-Object -Line).Lines
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SmartCon Refactoring Validation Audit" -ForegroundColor Cyan
Write-Host "  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# -- Check 1: Build verification -------------------------------------------
Write-Host "-- Check 1: Build verification -----------------------------" -ForegroundColor Yellow

if ($SkipBuild) {
    Write-Check "Build (skipped)" $true "SkipBuild flag"
} else {
    $buildOutput = dotnet build $SolutionPath 2>&1
    $buildExit = $LASTEXITCODE
    $errorCount = ($buildOutput | Where-Object { "$_" -match "error CS" } | Measure-Object).Count
    $warningCount = ($buildOutput | Where-Object { "$_" -match "warning CS" } | Measure-Object).Count
    $buildPass = $buildExit -eq 0 -and $errorCount -eq 0
    Write-Check "Build (dotnet build)" $buildPass "errors=$errorCount, warnings=$warningCount, exit=$buildExit"
    if ($Verbose -and $buildOutput) {
        $buildOutput | Where-Object { "$_" -match "error CS" } | ForEach-Object {
            Write-Host "         $_" -ForegroundColor Red
        }
    }
}

# -- Check 2: Test verification --------------------------------------------
Write-Host ""
Write-Host "-- Check 2: Test verification -----------------------------" -ForegroundColor Yellow

if ($SkipTests) {
    Write-Check "Tests (skipped)" $true "SkipTests flag"
} else {
    $testOutput = dotnet test $SolutionPath --no-build 2>&1
    $testExit = $LASTEXITCODE
    $totalLine = $testOutput | Where-Object { "$_" -match "Total.*Passed.*Failed" } | Select-Object -Last 1
    $testDetail = if ($totalLine) { "$totalLine".Trim() } else { "exit=$testExit" }
    Write-Check "Tests (dotnet test)" ($testExit -eq 0) $testDetail
}

# -- Check 3: I-09 -- Core doesn't call Revit API methods ------------------
Write-Host ""
Write-Host "-- Check 3: I-09 -- SmartCon.Core purity ------------------" -ForegroundColor Yellow

$forbiddenPatterns = @(
    "doc\.GetElement\(",
    "\.get_Parameter\(",
    "\.LookupParameter\(",
    "new Transaction\(",
    "new TransactionGroup\(",
    "doc\.Regenerate\(",
    "FilteredElementCollector",
    "using Autodesk\.Revit\.UI;",
    "using System\.Windows;"
)

$i09Violations = @()
$coreCsFiles = Get-ChildItem -Path "src/SmartCon.Core" -Filter "*.cs" -Recurse
foreach ($file in $coreCsFiles) {
    $relPath = $file.FullName.Replace("$rootDir\", "")
    foreach ($pattern in $forbiddenPatterns) {
        try {
            $found = Select-String -Path $file.FullName -Pattern $pattern -ErrorAction SilentlyContinue
            if ($found) {
                foreach ($f in $found) {
                    $lineText = "$($f.Line)".Trim()
                    if ($lineText.StartsWith("/") -or $lineText.StartsWith("*")) { continue }
                    $i09Violations += "$relPath`:$($f.LineNumber) : $pattern"
                }
            }
        } catch { }
    }
}

Write-Check "I-09: No Revit API calls in Core" ($i09Violations.Count -eq 0) "violations=$($i09Violations.Count)"
if ($i09Violations.Count -gt 0) {
    foreach ($v in $i09Violations) { Write-Host "         $v" -ForegroundColor Red }
}

# -- Check 4: I-03 -- No new Transaction(doc) outside service classes ------
Write-Host ""
Write-Host "-- Check 4: I-03 -- Transaction discipline ---------------" -ForegroundColor Yellow

$allowedTransactionFiles = @(
    "RevitTransactionService",
    "TransactionGroupSession",
    "FittingCtcManager",
    "RevitFittingInsertService",
    "RevitFamilyConnectorService",
    "ITransactionService"
)

$i03Violations = @()
Get-ChildItem -Path "src" -Filter "*.cs" -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace("$rootDir\", "")
    $isAllowed = $false
    foreach ($exc in $allowedTransactionFiles) {
        if ($rel -like "*$exc*") { $isAllowed = $true; break }
    }
    if ($isAllowed) { return }

    $content = Get-Content $_.FullName -Raw
    if ($content -match "new Transaction\s*\(" -or $content -match "new TransactionGroup\s*\(") {
        $i03Violations += $rel
    }
}
$i03Violations = $i03Violations | Select-Object -Unique

Write-Check "I-03: No raw Transaction outside services" ($i03Violations.Count -eq 0) "violations=$($i03Violations.Count)"
if ($i03Violations.Count -gt 0) {
    foreach ($v in $i03Violations) { Write-Host "         $v" -ForegroundColor Red }
}

# -- Check 5: I-05 -- No Element/Connector fields stored -------------------
Write-Host ""
Write-Host "-- Check 5: I-05 -- No Element/Connector field storage ---" -ForegroundColor Yellow

$i05Patterns = @(
    "private\s+(?:readonly\s+)?Element\s+\w+",
    "private\s+(?:readonly\s+)?Connector\s+\w+",
    "private\s+(?:readonly\s+)?FamilySymbol\s+\w+"
)

$i05Violations = @()
Get-ChildItem -Path "src/SmartCon.PipeConnect" -Filter "*.cs" -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace("$rootDir\", "")
    foreach ($pat in $i05Patterns) {
        $found = Select-String -Path $_.FullName -Pattern $pat
        foreach ($f in $found) {
            $i05Violations += "$rel`:$($f.LineNumber): $($f.Line.Trim())"
        }
    }
}

Write-Check "I-05: No Element/Connector fields" ($i05Violations.Count -eq 0) "violations=$($i05Violations.Count)"
if ($i05Violations.Count -gt 0) {
    foreach ($v in $i05Violations) { Write-Host "         $v" -ForegroundColor Red }
}

# -- Check 6: Magic number -- no bare 304.8 outside Constants and tests ---
Write-Host ""
Write-Host "-- Check 6: Magic number 304.8 ----------------------------" -ForegroundColor Yellow

$magicNumberFiles = @()
Get-ChildItem -Path "src" -Filter "*.cs" -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace("$rootDir\", "")
    if ($rel -like "*Constants.cs") { return }
    if ($rel -like "*Tests*") { return }

    $lines = Get-Content $_.FullName
    $lineNum = 0
    foreach ($line in $lines) {
        $lineNum++
        if ($line -match "304\.8" -and $line -notmatch "FeetToMm|MmToFeet") {
            $magicNumberFiles += "$rel`:$lineNum"
        }
    }
}

Write-Check "No bare 304.8 outside Constants/Tests" ($magicNumberFiles.Count -eq 0) "violations=$($magicNumberFiles.Count)"
if ($magicNumberFiles.Count -gt 0) {
    foreach ($v in $magicNumberFiles) { Write-Host "         $v" -ForegroundColor Yellow }
}

# -- Check 7: File size -- warn if ViewModel > 400 lines ------------------
Write-Host ""
Write-Host "-- Check 7: File size (ViewModels) ------------------------" -ForegroundColor Yellow

$sizeWarns = @()
Get-ChildItem -Path "src" -Filter "*ViewModel*.cs" -Recurse | ForEach-Object {
    $lines = Count-Lines $_.FullName
    $rel = $_.FullName.Replace("$rootDir\", "")
    if ($lines -gt 400) {
        $sizeWarns += "$rel ($lines lines)"
    }
}

$sizePass = $sizeWarns.Count -eq 0
Write-Check "ViewModel files <= 400 lines" $sizePass "warnings=$($sizeWarns.Count)"
if ($sizeWarns.Count -gt 0) {
    foreach ($w in $sizeWarns) { Write-Host "         $w" -ForegroundColor Yellow }
}

# -- Check 8: Dead code -- public methods in Services not called elsewhere -
Write-Host ""
Write-Host "-- Check 8: Dead code scan --------------------------------" -ForegroundColor Yellow

$allSrcText = @()
Get-ChildItem -Path "src" -Filter "*.cs" -Recurse | ForEach-Object {
    $allSrcText += Get-Content $_.FullName -Raw
}
$allContentBlob = $allSrcText -join "`n"

$deadCode = @()
$skipMethodNames = @("Dispose", "ToString", "Equals", "GetHashCode")

Get-ChildItem -Path "src/SmartCon.PipeConnect/Services" -Filter "*.cs" -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace("$rootDir\", "")
    $lines = Get-Content $_.FullName
    $lineNum = 0

    foreach ($line in $lines) {
        $lineNum++
        $trimmed = $line.Trim()

        if ($trimmed -notmatch "^public\s+" -and $trimmed -notmatch "^internal\s+") { continue }
        if ($trimmed -match "\bclass\b|\brecord\b|\binterface\b|\bstruct\b|\benum\b") { continue }

        $methodName = ""
        if ($trimmed -match "\b(\w+)\s*\(\s*$") {
            $methodName = $Matches[1]
        } elseif ($trimmed -match "\b(\w+)\s*\(") {
            $methodName = $Matches[1]
        }
        if (-not $methodName) { continue }

        $firstChar = $methodName.Substring(0, 1)
        if ($firstChar -cmatch "[a-z]") { continue }
        if ($skipMethodNames -contains $methodName) { continue }
        if ($methodName.Length -lt 3) { continue }

        $searchPat = "\b$methodName\b\s*\("
        $occurrences = [regex]::Matches($allContentBlob, $searchPat)
        if ($occurrences.Count -le 1) {
            $deadCode += "$rel`:$lineNum : $methodName (only $($occurrences.Count) occurrence total)"
        }
    }
}

$deadCodePass = $deadCode.Count -eq 0
Write-Check "No dead code in Services" $deadCodePass "warnings=$($deadCode.Count)"
if ($deadCode.Count -gt 0) {
    foreach ($d in $deadCode) { Write-Host "         $d" -ForegroundColor Yellow }
}

# -- Summary ---------------------------------------------------------------
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SUMMARY" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$passCount = ($results.Values | Where-Object { $_.Pass }).Count
$failCount = ($results.Values | Where-Object { -not $_.Pass }).Count
$totalCount = $results.Count

Write-Host ""
foreach ($name in $results.Keys) {
    $r = $results[$name]
    $icon = if ($r.Pass) { "[PASS]" } else { "[FAIL]" }
    $color = if ($r.Pass) { "Green" } else { "Red" }
    Write-Host "  $icon " -ForegroundColor $color -NoNewline
    Write-Host "$name -- $($r.Detail)"
}

Write-Host ""
Write-Host "  Total: $passCount/$totalCount passed, $failCount failed" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
Write-Host ""

if ($failCount -gt 0) {
    Write-Host "  Action required: fix failing checks before merging." -ForegroundColor Red
    exit 1
} else {
    Write-Host "  All checks passed. Ready for manual smoke testing." -ForegroundColor Green
    exit 0
}
