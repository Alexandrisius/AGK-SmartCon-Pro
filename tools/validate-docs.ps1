<#
.SYNOPSIS
    Валидатор документации SmartCon.
    Проверяет, что каждый public тип в SmartCon.Core задокументирован в docs/domain/.

.DESCRIPTION
    Структура валидируемой документации:
        docs/domain/
            README.md
            glossary.md
            models/
                README.md
                <module>.md          # pipeconnect, family-manager, ...
            interfaces/
                README.md
                <module>.md

    Сканирует ВСЕ .cs файлы в src/SmartCon.Core/ (исключая bin/obj).
    Для каждого файла определяет primary type (по имени файла без .cs)
    и классифицирует:
        - interface I* -> ожидается в docs/domain/interfaces/<module>.md
        - иначе (class/record/struct/enum) -> ожидается в docs/domain/models/<module>.md

    Парсер использует state machine для пропуска содержимого внутри
    code-fence блоков ```...```.

.EXIT CODES
    0 — все проверки пройдены
    1 — найдены расхождения (модели/интерфейсы без документации)
    2 — структура docs/domain/ сломана (нет ожидаемых директорий/README)
#>

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$hasErrors = $false
$hasStructuralErrors = $false

function Report-Error($message) {
    Write-Host "  [ERROR] $message" -ForegroundColor Red
    $script:hasErrors = $true
}

function Report-StructuralError($message) {
    Write-Host "  [STRUCT] $message" -ForegroundColor Red
    $script:hasStructuralErrors = $true
}

function Report-Ok($message) {
    Write-Host "  [OK] $message" -ForegroundColor Green
}

function Report-Warn($message) {
    Write-Host "  [WARN] $message" -ForegroundColor Yellow
}

# Determine if a .cs file's primary type is an interface.
# Returns $true if the file's primary type is an interface, $false if it's a
# class/record/struct/enum/static-class.
# Strategy: find the type declaration whose name matches the file's basename
# (without .cs). If found, classify by declaration kind. If not found
# (e.g. the file contains a nested type or the primary type is elsewhere),
# fallback to filename convention (starts with I + uppercase).
function Test-IsInterfaceFile {
    param([string]$FilePath)
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($FilePath)
    $lines = Get-Content -LiteralPath $FilePath -Encoding UTF8 -ErrorAction SilentlyContinue
    $foundPrimary = $false
    $primaryIsInterface = $false

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('/*') -or $trimmed.StartsWith('*')) { continue }
        if ($trimmed -match '^\s*\[.*\]\s*$') { continue }
        if ($trimmed -match '^\s*namespace\s+') { continue }
        if ($trimmed -match '^\s*using\s+') { continue }

        # Match a top-level type declaration
        if ($trimmed -match '^\s*(public|internal)\s+.*\b(interface|class|record|struct|enum)\s+([A-Z]\w*)') {
            $declaredName = $matches[3]
            if ($declaredName -eq $baseName) {
                $keyword = $matches[2]
                $primaryIsInterface = ($keyword -eq 'interface')
                $foundPrimary = $true
                break
            }
        }
    }

    if ($foundPrimary) { return $primaryIsInterface }

    # Fallback: filename convention (starts with I + uppercase)
    if ($baseName.Length -gt 1 -and $baseName[0] -eq 'I' -and [char]::IsUpper($baseName[1])) { return $true }
    return $false
}

# Normalize heading text to extract a type name.
# Examples:
#   "ConnectorProxy"                                  -> "ConnectorProxy"
#   "FamilyInfo *(Phase 3C)*"                         -> "FamilyInfo"
#   "PendingUpdate / MultiVersionPendingUpdate"       -> "PendingUpdate"
#   "**NetworkSnapshot** / **NetworkSnapshotStore**"  -> "NetworkSnapshot"
#   "SizeTableRow / AllSizeRowsResult"                -> "SizeTableRow"
#   "ConnectionRecord"                                -> "ConnectionRecord"
#   "_Foo_"                                           -> "Foo"
#   "## IDialogService"                                -> "IDialogService"
function Get-NormalizedName {
    param([string]$Heading)

    if ([string]::IsNullOrWhiteSpace($Heading)) { return $null }
    $h = $Heading.Trim()

    # Strip emphasis markers: _italic_, **bold**, *italic*
    $h = $h -replace '\*\*([^\*]+)\*\*', '$1'
    $h = $h -replace '\*([^\*]+)\*',     '$1'
    $h = $h -replace '_([^_]+)_',         '$1'

    # Strip trailing phase tag: *(Phase N)*, *(...)*, *(System Families)*, etc.
    $h = $h -replace '\s*\(\s*Phase\s+[A-Za-z0-9]+\s*\)', ''
    $h = $h -replace '\s*\(\s*[A-Za-z][^\)]*\)\s*$', ''

    # Take first part if split by '/'
    $first = ($h -split '\s*/\s*')[0].Trim()
    if ([string]::IsNullOrWhiteSpace($first)) { return $null }

    # Skip generic section headers (not type names)
    $generic = @(
        'Models', 'Interfaces', 'Math Utilities', 'FamilyManager Models',
        'FamilyManager Interfaces', 'RBAC Models', 'RBAC Interfaces',
        'Drag & Drop Contracts', 'UI Contracts', 'Cross-cutting',
        'Cross-cutting Models', 'Cross-cutting Abstractions', 'Cross-cutting Utilities',
        'Updates', 'Math', 'DB Migration Models', 'DB Migration Abstractions',
        'DB Migration Utilities', 'System Families Models', 'System Families Interfaces'
    )
    if ($generic -contains $first) { return $null }

    return $first
}

# Extract module from YAML frontmatter.
function Get-FrontmatterModule {
    param([string[]]$Lines)
    if ($Lines.Count -lt 3) { return $null }
    if ($Lines[0] -notmatch '^\s*---\s*$') { return $null }
    for ($i = 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match '^\s*---\s*$') { break }
        if ($Lines[$i] -match '^\s*module\s*:\s*(\S+)\s*$') {
            return $matches[1].Trim()
        }
    }
    return $null
}

# Parse headings from a single Markdown file using state machine.
function Get-MdHeadings {
    param(
        [string]$FilePath,
        [string]$Module
    )

    $lines = Get-Content -LiteralPath $FilePath -Encoding UTF8
    $inFence = $false
    $results = @()
    $lineNum = 0

    foreach ($raw in $lines) {
        $lineNum++
        $line = $raw.TrimEnd()

        # Code-fence detection
        if ($line -match '^\s*```') {
            $inFence = -not $inFence
            continue
        }

        if ($inFence) { continue }

        # Match heading levels 2-4
        if ($line -match '^(#{2,4})\s+(.+?)\s*$') {
            $level = $matches[1].Length
            $text  = $matches[2]
            $name  = Get-NormalizedName $text
            if ($name) {
                $results += [PSCustomObject]@{
                    File     = $FilePath
                    Line     = $lineNum
                    Level    = $level
                    RawText  = $text
                    Name     = $name
                    Module   = $Module
                }
            }
        }
    }
    return $results
}

Write-Host "=== SmartCon Documentation Validator ===" -ForegroundColor Cyan
Write-Host ""

# --- 0. Structural checks ---
Write-Host "[0/4] Checking docs/domain/ structure..." -ForegroundColor Yellow

$modelsDir    = Join-Path $repoRoot 'docs\domain\models'
$interfacesDir = Join-Path $repoRoot 'docs\domain\interfaces'
$domainDir    = Join-Path $repoRoot 'docs\domain'
$readmeDomain = Join-Path $domainDir 'README.md'
$readmeModels = Join-Path $modelsDir 'README.md'
$readmeIfaces = Join-Path $interfacesDir 'README.md'

if (-not (Test-Path -LiteralPath $domainDir)) {
    Report-StructuralError "docs/domain/ not found"
}
if (-not (Test-Path -LiteralPath $modelsDir)) {
    Report-StructuralError "docs/domain/models/ not found (expected subdirectory)"
}
if (-not (Test-Path -LiteralPath $interfacesDir)) {
    Report-StructuralError "docs/domain/interfaces/ not found (expected subdirectory)"
}
if ((Test-Path -LiteralPath $modelsDir) -and -not (Test-Path -LiteralPath $readmeModels)) {
    Report-StructuralError "docs/domain/models/README.md not found"
}
if ((Test-Path -LiteralPath $interfacesDir) -and -not (Test-Path -LiteralPath $readmeIfaces)) {
    Report-StructuralError "docs/domain/interfaces/README.md not found"
}

if ($hasStructuralErrors) {
    Write-Host ""
    Write-Host "=== Validation FAILED (structural) ===" -ForegroundColor Red
    Write-Host "Run: docs/domain/README.md and ensure structure exists." -ForegroundColor Yellow
    exit 2
}
Report-Ok "Structure OK"

# --- 1. Scan code: ALL of SmartCon.Core ---
Write-Host ""
Write-Host "[1/4] Scanning src/SmartCon.Core/ (full tree, excluding bin/obj)..." -ForegroundColor Yellow

$coreDir = Join-Path $repoRoot 'src\SmartCon.Core'
$codeFiles = Get-ChildItem -LiteralPath $coreDir -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

# Classify each file
$codeModels = @{}   # Name -> FullName
$codeIfaces = @{}   # Name -> FullName
$skippedNested = @() # Files with multiple types (not primary type)

foreach ($file in $codeFiles) {
    $typeName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($typeName -match '^\.gitkeep$|^AssemblyInfo$|^GlobalUsings$|^ModuleInitializer$') { continue }

    $isInterface = Test-IsInterfaceFile -FilePath $file.FullName

    if ($isInterface) {
        if ($codeIfaces.ContainsKey($typeName)) {
            # Duplicate (shouldn't happen in well-formed code)
        } else {
            $codeIfaces[$typeName] = $file.FullName
        }
    } else {
        if ($codeModels.ContainsKey($typeName)) {
            # Duplicate
        } else {
            $codeModels[$typeName] = $file.FullName
        }
    }
}

Write-Host "      Found $($codeModels.Count) class/record/struct/enum file(s)" -ForegroundColor Gray
Write-Host "      Found $($codeIfaces.Count) interface file(s)" -ForegroundColor Gray

# --- 2. Parse documentation files ---
Write-Host ""
Write-Host "[2/4] Parsing documentation files..." -ForegroundColor Yellow

$docHeadings = @()
$mdFiles = @()
if (Test-Path -LiteralPath $modelsDir) {
    $mdFiles += Get-ChildItem -LiteralPath $modelsDir -Filter '*.md' -File -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $interfacesDir) {
    $mdFiles += Get-ChildItem -LiteralPath $interfacesDir -Filter '*.md' -File -ErrorAction SilentlyContinue
}
# Exclude README.md (index files, not type documentation)
$mdFiles = @($mdFiles | Where-Object { $_.Name -ne 'README.md' })

Write-Host "      Scanning $($mdFiles.Count) .md file(s)..." -ForegroundColor Gray

foreach ($file in $mdFiles) {
    $lines = Get-Content -LiteralPath $file.FullName -Encoding UTF8
    $module = Get-FrontmatterModule $lines
    $headings = Get-MdHeadings -FilePath $file.FullName -Module $module
    $parentDir = Split-Path -Leaf (Split-Path -Parent $file.FullName)
    foreach ($h in $headings) {
        $h | Add-Member -NotePropertyName 'Side' -NotePropertyValue $parentDir -Force
    }
    $docHeadings += $headings
}

$documentedModels = @{}
$documentedIfaces = @{}
foreach ($h in $docHeadings) {
    if ($h.Side -eq 'interfaces') {
        if (-not $documentedIfaces.ContainsKey($h.Name)) {
            $documentedIfaces[$h.Name] = $h
        }
    } else {
        if (-not $documentedModels.ContainsKey($h.Name)) {
            $documentedModels[$h.Name] = $h
        }
    }
}

Write-Host "      Found $($documentedModels.Count) documented model name(s)" -ForegroundColor Gray
Write-Host "      Found $($documentedIfaces.Count) documented interface name(s)" -ForegroundColor Gray

# --- 3. Validate ---
Write-Host ""
Write-Host "[3/4] Validating..." -ForegroundColor Yellow

# 3a. Models
$missingModels = @()
foreach ($name in $codeModels.Keys) {
    if (-not $documentedModels.ContainsKey($name)) {
        Report-Error "Type '$name' exists in Core but is NOT documented (expected in docs/domain/models/*.md)"
        $missingModels += $name
    }
}
if ($missingModels.Count -eq 0) {
    Report-Ok "All $($codeModels.Count) Core classes/records/structs/enums are documented"
} else {
    Write-Host "      Missing $($missingModels.Count) type(s)" -ForegroundColor Gray
}

# 3b. Interfaces
$missingIfaces = @()
foreach ($name in $codeIfaces.Keys) {
    if (-not $documentedIfaces.ContainsKey($name)) {
        Report-Error "Interface '$name' exists in Core but is NOT documented (expected in docs/domain/interfaces/*.md)"
        $missingIfaces += $name
    }
}
if ($missingIfaces.Count -eq 0) {
    Report-Ok "All $($codeIfaces.Count) Core interfaces are documented"
} else {
    Write-Host "      Missing $($missingIfaces.Count) interface(s)" -ForegroundColor Gray
}

# --- 4. Orphaned docs (warnings only) ---
Write-Host ""
Write-Host "[4/4] Detecting orphaned documentation (warnings)..." -ForegroundColor Yellow

$orphaned = @()
foreach ($name in $documentedModels.Keys) {
    if ($name -notin $codeModels.Keys -and $name -notin $codeIfaces.Keys) {
        $h = $documentedModels[$name]
        $orphaned += "[$($h.Module)] $name ($($h.File | Split-Path -Leaf):$($h.Line))"
    }
}
foreach ($name in $documentedIfaces.Keys) {
    if ($name -notin $codeModels.Keys -and $name -notin $codeIfaces.Keys) {
        $h = $documentedIfaces[$name]
        $orphaned += "[$($h.Module)] $name ($($h.File | Split-Path -Leaf):$($h.Line))"
    }
}
if ($orphaned.Count -gt 0) {
    Write-Host ""
    Write-Host "[info] Orphaned documentation (documented but not in Core code):" -ForegroundColor Yellow
    foreach ($o in ($orphaned | Sort-Object -Unique)) {
        Report-Warn "  $o"
    }
    Write-Host "      (these are warnings only - the documented type may be:" -ForegroundColor Gray
    Write-Host "       (a) a nested type in another file, (b) a method not a type," -ForegroundColor Gray
    Write-Host "       (c) hosted in a non-Core assembly like SmartCon.FamilyManager or SmartCon.Revit," -ForegroundColor Gray
    Write-Host "       (d) or the documentation is outdated and should be removed)" -ForegroundColor Gray
}

# --- Summary ---
Write-Host ""
if ($hasErrors) {
    Write-Host "=== Validation FAILED ===" -ForegroundColor Red
    Write-Host "Add missing types to docs/domain/ before committing." -ForegroundColor Yellow
    exit 1
} else {
    Write-Host "=== Validation PASSED ===" -ForegroundColor Green
    exit 0
}
