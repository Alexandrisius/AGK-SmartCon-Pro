<#
.SYNOPSIS
    Валидатор документации SmartCon.
    Проверяет соответствие docs/ и фактического кода в src/.

.EXIT CODES
    0 — все проверки пройдены
    1 — найдены расхождения
#>

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$hasErrors = $false

function Report-Error($message) {
    Write-Host "  [ERROR] $message" -ForegroundColor Red
    $script:hasErrors = $true
}

function Report-Ok($message) {
    Write-Host "  [OK] $message" -ForegroundColor Green
}

Write-Host "=== SmartCon Documentation Validator ===" -ForegroundColor Cyan
Write-Host ""

# --- 1. Validate models.md ---
Write-Host "[1/2] Checking models in docs/domain/models.md..." -ForegroundColor Yellow

$modelFiles = @()
$modelFiles += Get-ChildItem -Path "$repoRoot\src\SmartCon.Core\Models" -Filter "*.cs" -Recurse |
    Select-Object -ExpandProperty Name | ForEach-Object { $_ -replace '\.cs$', '' }
$modelFiles += Get-ChildItem -Path "$repoRoot\src\SmartCon.Core\Math" -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty Name | ForEach-Object { $_ -replace '\.cs$', '' }
$modelFiles = $modelFiles | Sort-Object -Unique

$modelsInDoc = @()
Get-Content "$repoRoot\docs\domain\models.md" | ForEach-Object {
    if ($_ -match '^#{2,3}\s+(.+)$') {
        $name = $matches[1].Trim() -replace '\s*\*\([^\)]+\)\*\s*$', ''
        if ($name -match '\bModels\s*$') { return }
        $parts = $name -split '\s*/\s*'
        foreach ($part in $parts) {
            $part = $part.Trim()
            if ($part -and $part -notmatch '^\s*$') {
                $modelsInDoc += $part
            }
        }
    }
}
$modelsInDoc = $modelsInDoc | Sort-Object -Unique

$missingModels = 0
foreach ($model in $modelFiles) {
    if ($model -notin $modelsInDoc) {
        Report-Error "Model '$model' exists in Core but is NOT documented in models.md"
        $missingModels++
    }
}

if ($missingModels -eq 0) {
    Report-Ok "All $($modelFiles.Count) Core models are documented"
} else {
    Write-Host "      Found $missingModels undocumented model(s) out of $($modelFiles.Count)" -ForegroundColor Gray
}

# --- 2. Validate interfaces.md ---
Write-Host "[2/2] Checking interfaces in docs/domain/interfaces.md..." -ForegroundColor Yellow

$interfaceFiles = Get-ChildItem -Path "$repoRoot\src\SmartCon.Core\Services\Interfaces" -Filter "*.cs" -Recurse |
    Select-Object -ExpandProperty Name | ForEach-Object { $_ -replace '\.cs$', '' } |
    Sort-Object -Unique

$interfacesInDoc = @()
Get-Content "$repoRoot\docs\domain\interfaces.md" | ForEach-Object {
    if ($_ -match '^##\s+(I\w+)' -or $_ -match '^###\s+(I\w+)') {
        $interfacesInDoc += $matches[1].Trim()
    }
}
$interfacesInDoc = $interfacesInDoc | Sort-Object -Unique

$missingInterfaces = 0
foreach ($iface in $interfaceFiles) {
    if ($iface -notin $interfacesInDoc) {
        Report-Error "Interface '$iface' exists in Core but is NOT documented in interfaces.md"
        $missingInterfaces++
    }
}

if ($missingInterfaces -eq 0) {
    Report-Ok "All $($interfaceFiles.Count) Core interfaces are documented"
} else {
    Write-Host "      Found $missingInterfaces undocumented interface(s) out of $($interfaceFiles.Count)" -ForegroundColor Gray
}

# --- Summary ---
Write-Host ""
if ($hasErrors) {
    Write-Host "=== Validation FAILED ===" -ForegroundColor Red
    Write-Host "Add missing models/interfaces to docs before committing." -ForegroundColor Yellow
    exit 1
} else {
    Write-Host "=== Validation PASSED ===" -ForegroundColor Green
    exit 0
}
