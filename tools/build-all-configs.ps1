#Requires -Version 5.1
<#
    SmartCon - parallel build of all 7 shipping configurations.

    Safe because every configuration now has isolated obj/bin and its own
    obj\<Configuration>\project.assets.json (per-config NuGet graph, see
    src\Directory.Build.props). Switching -c no longer rewrites another
    configuration's project.assets.json, so the six builds cannot invalidate
    each other.

    Usage:
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1
        powershell ... -ThrottleLimit 3       # limit concurrency
        powershell ... -Configurations Debug.R25,Debug.R27
#>
[CmdletBinding()]
param(
    [string[]]$Configurations = @('Debug.R25', 'Debug.R26', 'Debug.R27', 'Debug.R24', 'Debug.R23', 'Debug.R21', 'Debug.R19'),
    [int]$ThrottleLimit = 6,
    # Extra dotnet build args (e.g. -p:Version=... passed by release.ps1).
    [string[]]$AdditionalArgs = @(),
    [string]$Project
)

$ErrorActionPreference = 'Stop'

# `powershell -File script.ps1 -Configurations A,B,C` arrives as ONE string, not an array.
if ($Configurations.Count -eq 1 -and $Configurations[0] -like '*,*') {
    $Configurations = @($Configurations[0] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$root = Split-Path -Parent $PSScriptRoot
$project = if ($Project) { $Project } else { Join-Path $root 'src\SmartCon.App\SmartCon.App.csproj' }
if (-not (Test-Path $project)) {
    Write-Host "[ERROR] Project not found: $project" -ForegroundColor Red
    exit 1
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$logDir = Join-Path $env:TEMP 'smartcon-build'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

if ($ThrottleLimit -lt 1) { $ThrottleLimit = 1 }

$queue = [System.Collections.Generic.Queue[string]]::new()
foreach ($c in $Configurations) { $queue.Enqueue($c) }

$active = @()
$results = @()

function Start-ConfigBuild([string]$config) {
    $log = Join-Path $logDir ("{0}.log" -f $config)
    Remove-Item $log -ErrorAction SilentlyContinue

    $job = Start-Job -ScriptBlock {
        param($dotnet, $project, $config, $log, $root, $extraArgs)

        Set-Location $root
        $OutputEncoding = [System.Text.Encoding]::UTF8
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        & $dotnet build $project -c $config @extraArgs --verbosity quiet 2>&1 |
            Out-File -FilePath $log -Encoding utf8
        $code = $LASTEXITCODE
        $watch.Stop()

        [pscustomobject]@{
            ExitCode = $code
            Seconds  = [math]::Round($watch.Elapsed.TotalSeconds)
        }
    } -ArgumentList $dotnet, $project, $config, $log, $root, $AdditionalArgs

    Write-Host ("[BUILD] {0} started (log: {1})" -f $config, $log) -ForegroundColor Cyan
    [pscustomobject]@{
        Config = $config
        Job    = $job
        Log    = $log
    }
}

function Complete-ConfigBuild($item) {
    Wait-Job $item.Job | Out-Null
    $data = Receive-Job $item.Job
    Remove-Job $item.Job -Force

    $code = 1
    $seconds = 0
    if ($null -ne $data) {
        $code = [int]$data.ExitCode
        $seconds = [int]$data.Seconds
    }

    if ($code -eq 0) {
        Write-Host ("[OK]   {0} ({1} s)" -f $item.Config, $seconds) -ForegroundColor Green
    } else {
        Write-Host ("[FAIL] {0} ({1} s, exit {2})" -f $item.Config, $seconds, $code) -ForegroundColor Red
        if (Test-Path $item.Log) {
            Get-Content $item.Log -Tail 30 -Encoding UTF8 | ForEach-Object { Write-Host ("  {0}" -f $_) }
        }
    }

    $script:results += [pscustomobject]@{
        Config   = $item.Config
        ExitCode = $code
        Seconds  = $seconds
    }
}

try {
    while ($queue.Count -gt 0 -or $active.Count -gt 0) {
        while ($queue.Count -gt 0 -and $active.Count -lt $ThrottleLimit) {
            $active += Start-ConfigBuild $queue.Dequeue()
        }

        $done = Wait-Job -Job @($active | ForEach-Object { $_.Job }) -Any
        $item = $active | Where-Object { $_.Job.Id -eq $done.Id } | Select-Object -First 1
        Complete-ConfigBuild $item
        $active = @($active | Where-Object { $_.Job.Id -ne $done.Id })
    }
}
finally {
    foreach ($item in $active) {
        if ($item.Job) {
            Stop-Job $item.Job -ErrorAction SilentlyContinue
            Remove-Job $item.Job -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ''
Write-Host 'Build summary:'
$results | Sort-Object Config | Format-Table Config, ExitCode, Seconds -AutoSize | Out-String | Write-Host

$failed = @($results | Where-Object { $_.ExitCode -ne 0 })
if ($failed.Count -gt 0) {
    Write-Host ("{0} configuration(s) failed: {1}" -f $failed.Count, (($failed | ForEach-Object { $_.Config }) -join ', ')) -ForegroundColor Red
    Write-Host ("Logs: {0}" -f $logDir) -ForegroundColor Yellow
    exit 1
}

Write-Host ("All {0} configurations built successfully. Logs: {1}" -f $results.Count, $logDir) -ForegroundColor Green
exit 0