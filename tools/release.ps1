[CmdletBinding()]
param(
    [string]$Version,
    [switch]$AutoIncrement,
    [switch]$MinorIncrement,
    [switch]$MajorIncrement,
    [switch]$Prerelease,
    [string]$PrereleaseLabel = "beta",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$DryRun,
    [string]$Changelog,
    [string]$ChangelogFile,
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

function Get-RemoteTags($pattern) {
    $output = git ls-remote --tags origin "refs/tags/$pattern" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($output)) { return @() }
    $tags = @($output -split "`r?`n" | ForEach-Object {
        $line = $_.Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { return }
        $ref = ($line -split "\s+")[1]
        if ($ref -match "^refs/tags/(?<tag>.+)$") { $matches["tag"] }
    } | Where-Object { $_ -ne $null })
    return @($tags | Sort-Object)
}

if ($Prerelease) {
    # Prerelease mode: version stays the same, only tag gets prerelease suffix
    if ($Version) {
        $baseVersion = $Version.TrimStart('v')
        # If version already contains prerelease suffix, use as-is
        if ($baseVersion -match '-') {
            $newVersion = $baseVersion
        } else {
            # Find next prerelease number from REMOTE tags
            $existingTags = @(Get-RemoteTags "v$baseVersion-$PrereleaseLabel.*")
            $nextNum = 1
            if ($existingTags.Count -gt 0) {
                $lastTag = $existingTags[-1]
                if ($lastTag -match "\.(\d+)$") {
                    $nextNum = [int]$matches[1] + 1
                }
            }
            $newVersion = "$baseVersion-$PrereleaseLabel.$nextNum"
        }
    }
    else {
        # Auto-increment patch from current version, then add prerelease suffix
        $parts = $currentVersion.Split('.')
        $parts[2] = [int]$parts[2] + 1
        $nextVersion = "$($parts[0]).$($parts[1]).$($parts[2])"
        # Find next prerelease number from REMOTE tags
        $existingTags = @(Get-RemoteTags "v$nextVersion-$PrereleaseLabel.*")
        $nextNum = 1
        if ($existingTags.Count -gt 0) {
            $lastTag = $existingTags[-1]
            if ($lastTag -match "\.(\d+)$") {
                $nextNum = [int]$matches[1] + 1
            }
        }
        $newVersion = "$nextVersion-$PrereleaseLabel.$nextNum"
    }
    Write-Host "Pre-release mode enabled" -ForegroundColor Magenta
}
elseif ($Version) {
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

$dotnetVersionArgs = @()
if ($DryRun) {
    Write-Warn "DRY RUN - Version.txt, git tag/push and GitHub release will be skipped"
    $dotnetVersionArgs = @(
        "-p:Version=$newVersion",
        "-p:AssemblyVersion=$newVersion.0",
        "-p:FileVersion=$newVersion.0",
        "-p:InformationalVersion=$newVersion"
    )
}
elseif ($Prerelease) {
    # Prerelease: Version.txt stays at stable, but assembly must have the prerelease version
    # AssemblyVersion/FileVersion must be numeric (no prerelease suffix)
    $prereleaseBaseVersion = $newVersion -replace '-.*$',''
    $dotnetVersionArgs = @(
        "-p:Version=$newVersion",
        "-p:AssemblyVersion=$prereleaseBaseVersion.0",
        "-p:FileVersion=$prereleaseBaseVersion.0",
        "-p:InformationalVersion=$newVersion"
    )
}

if (-not $Changelog -and -not $DryRun) {
    if ($ChangelogFile -and (Test-Path $ChangelogFile)) {
        $Changelog = Get-Content -Path $ChangelogFile -Raw
        Write-Host "Changelog loaded from $ChangelogFile" -ForegroundColor Green
    }
    else {
        Write-Host ""
        Write-Host "Enter changelog for v$newVersion" -ForegroundColor Cyan
        Write-Host "  Type or paste text, then press Enter twice to finish:" -ForegroundColor Gray
        Write-Host ""
        
        $changelogLines = [System.Collections.Generic.List[string]]::new()
        $blankCount = 0
        while ($true) {
            $line = Read-Host "  "
            if ([string]::IsNullOrWhiteSpace($line)) {
                $blankCount++
                if ($blankCount -ge 2) { break }
                if ($blankCount -eq 1 -and $changelogLines.Count -eq 0) { 
                    # First line is blank - user wants to skip
                    $Changelog = ""
                    break
                }
            }
            else {
                $blankCount = 0
                $changelogLines.Add($line)
            }
        }
        if ($changelogLines.Count -gt 0) {
            $Changelog = $changelogLines -join "`r`n"
        }
    }
}

# --- 1. Update Version.txt ---
if ($Prerelease) {
    Write-Step "Skipping Version.txt update (Prerelease)"
    Write-Ok "Version.txt preserved at $currentVersion"
}
elseif ($DryRun) {
    Write-Step "Skipping Version.txt update (DryRun)"
    Write-Ok "Version.txt preserved"
}
else {
    Write-Step "Updating Version.txt => $newVersion"
    Set-Content -Path $VersionFile -Value $newVersion -NoNewline
    Write-Ok "Version.txt updated"
}

# --- 2. Build All Versions ---
# Shipping artifacts:
# R19  = Revit 2019-2020 (net48, RevitAPI 2020)
# R21  = Revit 2021-2022 (net48, RevitAPI 2021 baseline, shared binary)
# R23  = Revit 2023      (net48 — own binary: first version with the AI agent)
# R24  = Revit 2024      (net48, separate binary because of API / ElementId changes)
# R25  = Revit 2025      (net8.0-windows, RevitAPI 2025)
# R26  = Revit 2026      (net8.0-windows, RevitAPI 2026 — Conductor* wire model, #233;
#                         R25-бинарник на 2026 НЕ работает: wire-свойства WireType сменили типы)
# R27  = Revit 2027      (net10.0-windows — Revit 2027 перешёл на .NET 10, SDK 10 из global.json)
$buildConfigs = @(
    @{ Config = "Release.R19"; Tfm = "net48";            Label = "Revit 2019-2020" }
    @{ Config = "Release.R21"; Tfm = "net48";            Label = "Revit 2021-2022" }
    @{ Config = "Release.R23"; Tfm = "net48";            Label = "Revit 2023" }
    @{ Config = "Release.R24"; Tfm = "net48";            Label = "Revit 2024" }
    @{ Config = "Release.R25"; Tfm = "net8.0-windows";   Label = "Revit 2025" }
    @{ Config = "Release.R26"; Tfm = "net8.0-windows";   Label = "Revit 2026" }
    @{ Config = "Release.R27"; Tfm = "net10.0-windows";  Label = "Revit 2027" }
)

# Параллельная сборка (тот же движок, что у build-and-deploy.bat). Последовательный
# прогон 7 x cold Release занимал ~30 минут; параллельный делает ту же работу за 6-8.
# Per-config логи: %TEMP%\smartcon-build\Release.RXX.log
$buildScript = Join-Path $PSScriptRoot "build-all-configs.ps1"
$releaseConfigs = @($buildConfigs | ForEach-Object { $_.Config })
Write-Step "Building all $($releaseConfigs.Count) configurations in parallel"
& $buildScript -Configurations $releaseConfigs -AdditionalArgs $dotnetVersionArgs -ThrottleLimit $releaseConfigs.Count
if ($LASTEXITCODE -ne 0) {
    Write-Err "One or more configurations failed - see per-config logs in %TEMP%\smartcon-build"
    throw "Release build failed"
}
Write-Ok "All $($releaseConfigs.Count) configurations built"

# --- 3. Run tests ---
if (-not $SkipTests) {
    Write-Step "Running tests"
    dotnet test $SrcDir\SmartCon.Tests\SmartCon.Tests.csproj -c Release.R25 @dotnetVersionArgs --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Err "Tests failed!"; throw "Tests failed!" }
    Write-Ok "All tests passed"
}

# --- 4. Publish SmartCon.Updater (once, net8.0) ---
Write-Step "Publishing SmartCon.Updater (net8.0)"
$updaterProject = Join-Path $SrcDir "SmartCon.Updater\SmartCon.Updater.csproj"
$updaterPublishDir = Join-Path $ArtifactsDir "publish\SmartCon.Updater"
if (Test-Path $updaterPublishDir) { Remove-Item $updaterPublishDir -Recurse -Force }

dotnet publish $updaterProject -c Release -f net8.0 @dotnetVersionArgs --nologo -v q -o $updaterPublishDir
if ($LASTEXITCODE -ne 0) { throw "Publish SmartCon.Updater failed!" }
Write-Ok "SmartCon.Updater published"

# --- 5. Publish All SmartCon Versions (parallel per configuration) ---
$zipPaths = @()
$logDir = Join-Path $env:TEMP "smartcon-build"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
$appProject = Join-Path $SrcDir "SmartCon.App\SmartCon.App.csproj"
$mergedListPath = Join-Path $SrcDir "SmartCon.App\Resources\merged-dependencies.txt"

$publishScript = {
    param($dotnet, $project, $config, $tfm, $publishDir, $zipPath, $versionArgs, $updaterDir,
          $isNet48, $mergedListPath, $root, $log)

    Set-Location $root
    $OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $watch = [System.Diagnostics.Stopwatch]::StartNew()

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    & $dotnet publish $project -c $config -f $tfm @versionArgs --nologo -v q -o $publishDir 2>&1 |
        Out-File -FilePath $log -Encoding utf8
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $watch.Stop()
        return [pscustomobject]@{ ExitCode = $code; Seconds = [math]::Round($watch.Elapsed.TotalSeconds); SizeMB = 0; Log = $log }
    }

    # Copy Updater into each ZIP artifact (for backwards compat and standalone ZIP usage)
    Copy-Item (Join-Path $updaterDir "SmartCon.Updater.exe") $publishDir -Force
    Copy-Item (Join-Path $updaterDir "SmartCon.Updater.dll") $publishDir -Force
    foreach ($updaterFile in @("SmartCon.Updater.deps.json", "SmartCon.Updater.runtimeconfig.json")) {
        $candidate = Join-Path $updaterDir $updaterFile
        if (Test-Path $candidate) { Copy-Item $candidate $publishDir -Force }
    }

    # ADR-051 (Issue #134): obsolete-files.txt — список dll, сшитых ILRepack в SmartCon.Dependencies.
    # Updater и installer удаляют эти файлы при обновлении/установке поверх старых версий.
    # ТОЛЬКО net48-архивы: на net8 те же имена нужны как loose-файлы и удалять их нельзя.
    if ($isNet48) { Copy-Item $mergedListPath (Join-Path $publishDir "obsolete-files.txt") -Force }

    # Remove RevitAPI/AdWindows from output (ExcludeAssets=runtime should handle this, but verify)
    foreach ($pattern in @("RevitAPI*.dll", "AdWindows*.dll", "UIAutomation*.dll")) {
        Get-ChildItem -Path $publishDir -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
    $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

    $watch.Stop()
    [pscustomobject]@{ ExitCode = 0; Seconds = [math]::Round($watch.Elapsed.TotalSeconds); SizeMB = $sizeMB; Log = $log }
}

Write-Step "Publishing $($buildConfigs.Count) artifacts in parallel"
$publishJobs = @()
foreach ($bc in $buildConfigs) {
    $tag = $bc.Config.Split('.')[1]
    $publishDir = Join-Path $ArtifactsDir "publish\SmartCon-$tag"
    $zipPath = Join-Path $ArtifactsDir "SmartCon-$newVersion-$tag.zip"
    $log = Join-Path $logDir ("publish.{0}.log" -f $tag)
    Remove-Item $log -ErrorAction SilentlyContinue
    $isNet48 = $tag -in @("R19", "R21", "R23", "R24")

    $publishJobs += [pscustomobject]@{
        Tag = $tag
        Zip = $zipPath
        Log = $log
        Job = Start-Job -ScriptBlock $publishScript -ArgumentList `
            $dotnetExe, $appProject, $bc.Config, $bc.Tfm, $publishDir, $zipPath, $dotnetVersionArgs, `
            $updaterPublishDir, $isNet48, $mergedListPath, $RootDir, $log
    }
    Write-Host ("[PUBLISH] {0} started (log: {1})" -f $tag, $log) -ForegroundColor Cyan
}

foreach ($pj in $publishJobs) {
    Wait-Job $pj.Job | Out-Null
    $data = Receive-Job $pj.Job
    Remove-Job $pj.Job -Force
    $item = @($data)[0]
    if ($null -eq $item -or $item.ExitCode -ne 0) {
        Write-Err "Publish SmartCon [$($pj.Tag)] failed!"
        if (Test-Path $pj.Log) { Get-Content $pj.Log -Tail 20 -Encoding UTF8 | ForEach-Object { Write-Host "  $_" } }
        throw "Publish SmartCon [$($pj.Tag)] failed!"
    }
    Write-Ok ("SmartCon [{0}] published + zipped ({1} MB, {2} s)" -f $pj.Tag, $item.SizeMB, $item.Seconds)
    $zipPaths += $pj.Zip
}

Write-Step "All ZIP archives created"
foreach ($zp in $zipPaths) {
    Write-Ok "  $(Split-Path $zp -Leaf)"
}

# --- 6. Build Inno Setup installer ---
$installerName = "SmartCon-$newVersion-setup.exe"
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

if ($DryRun) {
    Write-Step "Skipping git commit/tag, push and GitHub Release (DryRun)"
    Write-Ok "Dry-run verification finished before VCS/network steps"
}
else {
    Write-Step "Git: commit + tag v$newVersion"
    if (-not $Prerelease) {
        git add $VersionFile
        git commit -m "release: v$newVersion"
        if ($LASTEXITCODE -ne 0) { Write-Warn "Nothing to commit?" }
    }
    # Remove stale local tag if it exists (previous run deleted on remote)
    $localTagCheck = git tag --list "v$newVersion" 2>$null
    if ($localTagCheck) {
        $null = git tag -d "v$newVersion" 2>$null
        Write-Warn "Removed stale local tag v$newVersion"
    }
    git tag "v$newVersion"
    Write-Ok "Tagged v$newVersion"

    Write-Step "Git: push to remote"
    $branch = git rev-parse --abbrev-ref HEAD
    git push origin $branch
    git push origin "v$newVersion"
    Write-Ok "Pushed $branch + tag v$newVersion"

    Write-Step "Creating GitHub Release"

    $notesFile = Join-Path $ArtifactsDir "release-notes-tmp.md"
    if (-not [string]::IsNullOrWhiteSpace($Changelog)) {
        [System.IO.File]::WriteAllText($notesFile, $Changelog, [System.Text.UTF8Encoding]::new($false))
        $notesFlag = @("--notes-file", $notesFile)
    } else {
        $notesFlag = @("--generate-notes")
    }

    $installerPath = Join-Path $ArtifactsDir $installerName
    $releaseAssets = @() + $zipPaths
    if ($installerBuilt -and (Test-Path $installerPath)) {
        $releaseAssets += $installerPath
    }
    $releaseArgs = @("release", "create", "v$newVersion") + $releaseAssets + @("--title", "SmartCon v$newVersion") + $notesFlag
    if ($Prerelease) {
        $releaseArgs += @("--prerelease")
        Write-Host "  Creating as PRE-RELEASE" -ForegroundColor Magenta
    }

    & gh @releaseArgs
    $ghExit = $LASTEXITCODE
    if (Test-Path $notesFile) { Remove-Item $notesFile -Force -ErrorAction SilentlyContinue }
    if ($ghExit -ne 0) { Write-Err "GitHub release creation failed!"; throw "gh release create failed" }
    Write-Ok "GitHub Release v$newVersion created"
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
if ($DryRun) {
    Write-Host "  Dry run for v$newVersion completed!" -ForegroundColor Green
}
else {
    Write-Host "  Release v$newVersion published!" -ForegroundColor Green
}
foreach ($zp in $zipPaths) {
    Write-Host "  ZIP: $(Split-Path $zp -Leaf)" -ForegroundColor White
}
if ($installerBuilt) { Write-Host "  EXE: $installerName" -ForegroundColor White }
if (-not $DryRun) {
    Write-Host "  URL: https://github.com/$GitHubOwner/$GitHubRepo/releases/tag/v$newVersion" -ForegroundColor Cyan
}
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
