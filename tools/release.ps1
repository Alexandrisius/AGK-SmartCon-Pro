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
    [string]$GitHubOwner = "AGK-Engineering",
    [string]$GitHubRepo = "AGK-SmartCon-Pro"
)

$ErrorActionPreference = "Stop"
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

# --- 2. Build Release ---
Write-Step "Building Release configuration"
dotnet build $SrcDir\SmartCon.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Err "Build failed!"; throw "Build failed!" }
Write-Ok "Build succeeded"

# --- 3. Run tests ---
if (-not $SkipTests) {
    Write-Step "Running tests"
    dotnet test $SrcDir\SmartCon.Tests\SmartCon.Tests.csproj -c Release --nologo -v q --no-build
    if ($LASTEXITCODE -ne 0) { Write-Err "Tests failed!"; throw "Tests failed!" }
    Write-Ok "All tests passed"
}

# --- 4. Publish ---
Write-Step "Publishing SmartCon.App"
$publishDir = Join-Path $ArtifactsDir "publish\SmartCon"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $SrcDir\SmartCon.App\SmartCon.App.csproj -c Release --nologo -v q --no-build -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Publish SmartCon.App failed!" }

Write-Step "Publishing SmartCon.Updater"
$updaterPublishDir = Join-Path $ArtifactsDir "publish\SmartCon.Updater"
if (Test-Path $updaterPublishDir) { Remove-Item $updaterPublishDir -Recurse -Force }

$updaterProject = Join-Path $SrcDir "SmartCon.Updater\SmartCon.Updater.csproj"
if (Test-Path $updaterProject) {
    dotnet publish $updaterProject -c Release --nologo -v q -o $updaterPublishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish SmartCon.Updater failed!" }

    Copy-Item "$updaterPublishDir\SmartCon.Updater.exe" "$publishDir\" -Force
    Copy-Item "$updaterPublishDir\SmartCon.Updater.dll" "$publishDir\" -Force
    $depsJson = "$updaterPublishDir\SmartCon.Updater.deps.json"
    $runtimeJson = "$updaterPublishDir\SmartCon.Updater.runtimeconfig.json"
    if (Test-Path $depsJson) { Copy-Item $depsJson "$publishDir\" -Force }
    if (Test-Path $runtimeJson) { Copy-Item $runtimeJson "$publishDir\" -Force }
    Write-Ok "SmartCon.Updater published"
}

Write-Ok "All published to $publishDir"

# --- 5. Create ZIP ---
Write-Step "Creating ZIP archive"
$zipName = "SmartCon-$newVersion.zip"
$zipPath = Join-Path $ArtifactsDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
$sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Ok "Created $zipName ($sizeMB MB)"

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
    Set-Content -Path $notesFile -Value $Changelog -NoNewline
    $notesFlag = @("--notes-file", $notesFile)
} else {
    $notesFlag = @("--generate-notes")
}

$installerName = "SmartCon-$newVersion-setup.exe"
$installerPath = Join-Path $ArtifactsDir $installerName
if ($installerBuilt -and (Test-Path $installerPath)) {
    $releaseArgs = @("release", "create", "v$newVersion", $zipPath, $installerPath, "--title", "SmartCon v$newVersion") + $notesFlag
} else {
    $releaseArgs = @("release", "create", "v$newVersion", $zipPath, "--title", "SmartCon v$newVersion") + $notesFlag
}

& gh @releaseArgs
$ghExit = $LASTEXITCODE
if (Test-Path $notesFile) { Remove-Item $notesFile -Force -ErrorAction SilentlyContinue }
if ($ghExit -ne 0) { Write-Err "GitHub release creation failed!"; throw "gh release create failed" }
Write-Ok "GitHub Release v$newVersion created"

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  Release v$newVersion published!" -ForegroundColor Green
Write-Host "  ZIP: $zipName" -ForegroundColor White
if ($installerBuilt) { Write-Host "  EXE: $installerName" -ForegroundColor White }
Write-Host "  URL: https://github.com/$GitHubOwner/$GitHubRepo/releases/tag/v$newVersion" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Green
Write-Host ""