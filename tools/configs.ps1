#Requires -Version 5.1
<#
    Single source of truth for the shipping build configurations.

    The ONLY switch is <SmartConEnableRevit2027> in src\Directory.Build.props.
    Set it to true to bring Revit 2027 (net10) back — every script below, the
    local sweep and the release pipeline pick it up automatically.

    net10 builds require the .NET 10 SDK, which is NOT the repo default anymore
    (global.json pins 8.0.4xx). Those builds are therefore executed from
    tools\sdk10, which carries its own global.json pinning SDK 10.0.4xx.
#>

function Get-SmartConRepoRoot {
    param([string]$Root)
    if ($Root) { return $Root }
    return (Split-Path -Parent $PSScriptRoot)
}

function Test-SmartConRevit2027Enabled {
    param([string]$Root)
    $rootDir = Get-SmartConRepoRoot -Root $Root
    $props = Join-Path $rootDir 'src\Directory.Build.props'
    if (-not (Test-Path $props)) { return $false }
    $text = Get-Content $props -Raw
    $match = [regex]::Match($text, '<SmartConEnableRevit2027(?:\s[^>]*)?>\s*([^<]+?)\s*</SmartConEnableRevit2027>')
    return ($match.Success -and $match.Groups[1].Value.Trim() -ieq 'true')
}

function Get-SmartConConfigs {
    param(
        [ValidateSet('Debug', 'Release')][string]$Kind = 'Debug',
        [string]$Root
    )
    $list = @()
    foreach ($r in @('R19', 'R21', 'R23', 'R24', 'R25', 'R26')) { $list += ("{0}.{1}" -f $Kind, $r) }
    if (Test-SmartConRevit2027Enabled -Root $Root) { $list += ("{0}.R27" -f $Kind) }
    return $list
}

# net10 (R27) is the only configuration that needs SDK 10 → build it from tools\sdk10.
function Get-SmartConBuildWorkingDir {
    param([string]$Config, [string]$Root)
    $rootDir = Get-SmartConRepoRoot -Root $Root
    if ($Config -like '*.R27') { return (Join-Path $rootDir 'tools\sdk10') }
    return $rootDir
}