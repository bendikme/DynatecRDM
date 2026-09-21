#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes Remote Desktop Manager into a folder that is ready to package.

.DESCRIPTION
    Produces a plain folder (not a single file): the MSI installs a folder, single-file
    publishing would throw away the ReadyToRun start-up win, and the portable zip is the same
    folder compressed.

    This never touches %LOCALAPPDATA%\DynatecRDM - the database, snapshots and logs live
    there and are completely separate from the installation. -OutDir is refused if it points
    into that folder, because the publish folder is emptied before it is written.

.EXAMPLE
    .\build\publish.ps1 -Version 1.2.3
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [bool]$SelfContained = $true,
    [string]$OutDir = "$PSScriptRoot\..\artifacts\publish"
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DynatecRDM\DynatecRDM.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    Fail "Project not found: $project"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'The .NET SDK ("dotnet") is not on PATH. Install the .NET 10 SDK and try again.'
}

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?$') {
    Fail "-Version '$Version' is not a version number (expected 1.2.3)."
}

try {
    $OutDir = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\')
} catch {
    Fail "-OutDir '$OutDir' is not a usable path."
}

# Guard against a mistyped -OutDir wiping something important: this folder gets deleted.
if ($OutDir.Length -le 3 -or $OutDir -eq $repoRoot.TrimEnd('\')) {
    Fail "-OutDir '$OutDir' is not a safe publish folder; it is emptied before publishing."
}

# The database, snapshots and logs live in %LOCALAPPDATA%\DynatecRDM. Publishing into it - or
# into anything under it - would delete user data, so that is refused outright.
if ($env:LOCALAPPDATA) {
    $dataDir = (Join-Path $env:LOCALAPPDATA 'DynatecRDM').TrimEnd('\')
    if ($OutDir -eq $dataDir -or $OutDir.StartsWith($dataDir + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "-OutDir '$OutDir' is inside the application data folder '$dataDir'. Publishing there would destroy the user database."
    }
}

if (Test-Path -LiteralPath $OutDir) {
    try {
        Remove-Item -LiteralPath $OutDir -Recurse -Force
    } catch {
        Fail "Could not empty '$OutDir': $($_.Exception.Message). Close anything running from that folder and try again."
    }
}

try {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
} catch {
    Fail "Could not create '$OutDir': $($_.Exception.Message)"
}

$dotnetArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $OutDir,
    '--nologo',
    '-v', 'minimal',
    '-p:PublishReadyToRun=true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none'
)

if ($SelfContained) {
    $dotnetArgs += '--self-contained'
} else {
    $dotnetArgs += '--no-self-contained'
}

if ($Version) {
    # AssemblyVersion and FileVersion only accept numbers, so drop any -prerelease suffix.
    # InformationalVersion keeps the full string: that is what the updater compares against
    # the GitHub release tag.
    $numericVersion = ($Version -split '-')[0]
    $dotnetArgs += @(
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion",
        "-p:FileVersion=$numericVersion",
        "-p:InformationalVersion=$Version"
    )
}

$banner = "Publishing $Configuration | $Runtime"
if ($Version) { $banner += " | $Version" }
Write-Host $banner -ForegroundColor Cyan
& dotnet @dotnetArgs

if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed with exit code $LASTEXITCODE. See the build output above."
}

$exe = Join-Path $OutDir 'DynatecRDM.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Fail "dotnet publish reported success but $exe is missing."
}

$files = @(Get-ChildItem -LiteralPath $OutDir -Recurse -File)
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ''
Write-Host "Output : $OutDir"
Write-Host "Files  : $($files.Count)"
Write-Host "Size   : $([math]::Round($totalBytes / 1MB, 1)) MB"
