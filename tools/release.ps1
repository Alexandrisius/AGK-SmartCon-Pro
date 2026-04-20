[CmdletBinding()]
param(
    [string]$Version,
    [switch]$AutoIncrement,
    [switch]$MinorIncrement,
    [switch]$MajorIncrement,
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$DryRun,
    [string]$Changelog,
    [string]$GitHubOwner = "Alexandrisius",
    [string]$GitHubRepo = "AGK-SmartCon-Pro"
)

$ErrorActionPreference = "Stop"

[Console]::InputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$RootDir = Split-Path -Parent $PSScriptRoot
$VersionFile = Join-Path $RootDir "Version.txt"
$ArtifactsDir = Join-Path $RootDir "artifacts"
$SrcDir = Join-Path $RootDir "src"

function Write-Step($msg) { Write-Host "`n===> $msg" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "  OK: $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  WARN: $msg" -ForegroundColor Yellow }
function Write-Err($msg) { Write-Host "  ERROR: $msg" -ForegroundColor Red }

$currentVersion = (Get-Content $VersionFile -TotalCount 1).Trim()
Write-Host "Current version: $currentVersion" -ForegroundColor White

if ($Version) {
    $newVersion = $Version.TrimStart('v')
}
elseif ($AutoIncrement -or (-not $Version -and -not $MinorIncrement -and -not $MajorIncrement)) {
    $parts = $currentVersion.Split('.')
    $parts[2] = [int]$parts[2] + 1
    $newVersion = "$($parts[0]).$($parts[1]).$($parts[2])"
}
elseif ($MinorIncrement) {
    $parts = $currentVersion.Split('.')
    $parts[1] = [int]$parts[1] + 1
    $parts[2] = 0
    $newVersion = "$($parts[0]).$($parts[1]).$($parts[2])"
}
elseif ($MajorIncrement) {
    $parts = $currentVersion.Split('.')
    $parts[0] = [int]$parts[0] + 1
    $parts[1] = 0
    $parts[2] = 0
    $newVersion = "$($parts[0]).$($parts[1]).$($parts[2])"
}

Write-Host "New version: $newVersion" -ForegroundColor Yellow

if (-not $Changelog -and -not $DryRun) {
    Write-Host ""
    Write-Host "Enter changelog for v$newVersion (empty line to finish):" -ForegroundColor Cyan
    $changelogLines = [System.Collections.Generic.List[string]]::new()
    while ($true) {
        $line = Read-Host "  "
        if ([string]::IsNullOrWhiteSpace($line)) { break }
        $changelogLines.Add($line)
    }
    $Changelog = $changelogLines -join "`r`n"
}

if ($DryRun) {
    Write-Warn "DRY RUN - no changes will be made"
    Write-Host "Changelog: $Changelog"
    return
}

# --- 1. Update Version.txt ---
Write-Step "Updating Version.txt => $newVersion"
Set-Content -Path $VersionFile -Value $newVersion -NoNewline
Write-Ok "Version.txt updated"

# --- 2. Build All Versions ---
# Shipping artifacts:
# R19  = Revit 2019-2020 (net48, RevitAPI 2020)
# R21  = Revit 2021-2023 (net48, RevitAPI 2021 baseline, single shared binary)
# R24  = Revit 2024      (net48, separate binary because of API / ElementId changes)
# R25  = Revit 2025      (net8.0-windows)
$buildConfigs = @(
    @{ Config = "Release.R19"; Tfm = "net48";           Label = "Revit 2019-2020" }
    @{ Config = "Release.R21"; Tfm = "net48";           Label = "Revit 2021-2023" }
    @{ Config = "Release.R24"; Tfm = "net48";           Label = "Revit 2024" }
    @{ Config = "Release.R25"; Tfm = "net8.0-windows";  Label = "Revit 2025" }
)

foreach ($bc in $buildConfigs) {
    Write-Step "Building [$($bc.Label)] $($bc.Config) / $($bc.Tfm)"
    dotnet build $SrcDir\SmartCon.App\SmartCon.App.csproj -c $bc.Config -f $bc.Tfm --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Err "Build $($bc.Config) failed!"; throw "Build $($bc.Config) failed!" }
    Write-Ok "$($bc.Config) build succeeded"
}

# --- 3. Run tests ---
if (-not $SkipTests) {
    Write-Step "Running tests"
    dotnet test $SrcDir\SmartCon.Tests\SmartCon.Tests.csproj -c Release.R25 --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Err "Tests failed!"; throw "Tests failed!" }
    Write-Ok "All tests passed"
}

# --- 4. Publish SmartCon.Updater (once, net8.0) ---
Write-Step "Publishing SmartCon.Updater (net8.0)"
$updaterProject = Join-Path $SrcDir "SmartCon.Updater\SmartCon.Updater.csproj"
$updaterPublishDir = Join-Path $ArtifactsDir "publish\SmartCon.Updater"
if (Test-Path $updaterPublishDir) { Remove-Item $updaterPublishDir -Recurse -Force }

dotnet publish $updaterProject -c Release -f net8.0 --nologo -v q -o $updaterPublishDir
if ($LASTEXITCODE -ne 0) { throw "Publish SmartCon.Updater failed!" }
Write-Ok "SmartCon.Updater published"

# --- 5. Publish All SmartCon Versions ---
$zipPaths = @()

foreach ($bc in $buildConfigs) {
    $tag = $bc.Config.Split('.')[1]
    Write-Step "Publishing SmartCon [$tag] ($($bc.Label))"

    $publishDir = Join-Path $ArtifactsDir "publish\SmartCon-$tag"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    dotnet publish $SrcDir\SmartCon.App\SmartCon.App.csproj `
        -c $bc.Config -f $bc.Tfm --nologo -v q -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish SmartCon [$tag] failed!" }

    # Copy Updater into each ZIP artifact (for backwards compat and standalone ZIP usage)
    Copy-Item "$updaterPublishDir\SmartCon.Updater.exe" "$publishDir\" -Force
    Copy-Item "$updaterPublishDir\SmartCon.Updater.dll" "$publishDir\" -Force
    $depsJson = "$updaterPublishDir\SmartCon.Updater.deps.json"
    $runtimeJson = "$updaterPublishDir\SmartCon.Updater.runtimeconfig.json"
    if (Test-Path $depsJson) { Copy-Item $depsJson "$publishDir\" -Force }
    if (Test-Path $runtimeJson) { Copy-Item $runtimeJson "$publishDir\" -Force }

    # Remove RevitAPI/AdWindows from output (ExcludeAssets=runtime should handle this, but verify)
    Remove-Item "$publishDir\RevitAPI*.dll" -ErrorAction SilentlyContinue
    Remove-Item "$publishDir\AdWindows*.dll" -ErrorAction SilentlyContinue
    Remove-Item "$publishDir\UIAutomation*.dll" -ErrorAction SilentlyContinue

    Write-Ok "SmartCon [$tag] published to $publishDir"

    $zipName = "SmartCon-$newVersion-$tag.zip"
    $zipPath = Join-Path $ArtifactsDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
    $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Ok "Created $zipName ($sizeMB MB)"
    $zipPaths += $zipPath
}

Write-Step "All ZIP archives created"
foreach ($zp in $zipPaths) {
    Write-Ok "  $(Split-Path $zp -Leaf)"
}

# --- 6. Build Inno Setup installer ---
$installerBuilt = $false
if (-not $SkipInstaller) {
    $issPath = Join-Path $PSScriptRoot "installer\SmartCon-Setup.iss"
    $isccPath = $null

    $isccCmd = Get-Command "ISCC" -ErrorAction SilentlyContinue
    if ($isccCmd) { $isccPath = $isccCmd.Source }
    else {
        $candidates = @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { $isccPath = $c; break }
        }
    }

    if ($isccPath) {
        Write-Step "Building Inno Setup installer"
        & $isccPath "/DAppVersion=$newVersion" $issPath
        if ($LASTEXITCODE -ne 0) { Write-Warn "Inno Setup build failed - skipping installer" }
        else { $installerBuilt = $true; Write-Ok "Installer created" }
    }
    else {
        Write-Warn "Inno Setup (ISCC) not found. Install from https://jrsoftware.org/isdl.php"
    }
}

# --- 7. Git commit + tag ---
Write-Step "Git: commit + tag v$newVersion"
git add $VersionFile
git commit -m "release: v$newVersion"
if ($LASTEXITCODE -ne 0) { Write-Warn "Nothing to commit?" }
git tag "v$newVersion"
Write-Ok "Tagged v$newVersion"

# --- 8. Push ---
Write-Step "Git: push to remote"
$branch = git rev-parse --abbrev-ref HEAD
git push origin $branch
git push origin "v$newVersion"
Write-Ok "Pushed $branch + tag v$newVersion"

# --- 9. GitHub Release ---
Write-Step "Creating GitHub Release"

$notesFile = Join-Path $ArtifactsDir "release-notes-tmp.md"
if (-not [string]::IsNullOrWhiteSpace($Changelog)) {
    [System.IO.File]::WriteAllText($notesFile, $Changelog, [System.Text.UTF8Encoding]::new($false))
    $notesFlag = @("--notes-file", $notesFile)
} else {
    $notesFlag = @("--generate-notes")
}

$installerName = "SmartCon-$newVersion-setup.exe"
$installerPath = Join-Path $ArtifactsDir $installerName
$releaseAssets = @() + $zipPaths
if ($installerBuilt -and (Test-Path $installerPath)) {
    $releaseAssets += $installerPath
}
$releaseArgs = @("release", "create", "v$newVersion") + $releaseAssets + @("--title", "SmartCon v$newVersion") + $notesFlag

& gh @releaseArgs
$ghExit = $LASTEXITCODE
if (Test-Path $notesFile) { Remove-Item $notesFile -Force -ErrorAction SilentlyContinue }
if ($ghExit -ne 0) { Write-Err "GitHub release creation failed!"; throw "gh release create failed" }
Write-Ok "GitHub Release v$newVersion created"

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  Release v$newVersion published!" -ForegroundColor Green
foreach ($zp in $zipPaths) {
    Write-Host "  ZIP: $(Split-Path $zp -Leaf)" -ForegroundColor White
}
if ($installerBuilt) { Write-Host "  EXE: $installerName" -ForegroundColor White }
Write-Host "  URL: https://github.com/$GitHubOwner/$GitHubRepo/releases/tag/v$newVersion" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
