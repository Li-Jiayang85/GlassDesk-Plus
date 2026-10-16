# Builds the user-scope installer for GlassDesk Plus.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.
#
# Steps: self-contained publish -> Inno Setup compile.
# The installer never touches the user's Data folder (the .iss excludes Data\*).
#
# IMPORTANT: running the produced installer can only be validated in an ordinary interactive
# desktop session. Inside a restricted/automation token the setup fails at the final write
# step with "Access denied" while creating files under %LOCALAPPDATA%\Programs\GlassDesk.
# See docs/self-contained-deploy.md.

[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.5',
    [string]$StageDirectory,
    [string]$InnoCompiler,
    # AppId is fixed by the .iss; exposed only so callers get the same guard error.
    [string]$AppId = 'GlassDesk.Independent'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-Dotnet {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "dotnet not found at '$Explicit'." }
        return $Explicit
    }
    if ($env:GLASSDESK_DOTNET -and (Test-Path $env:GLASSDESK_DOTNET)) { return $env:GLASSDESK_DOTNET }
    $known = 'D:\dotnet10\dotnet.exe'
    if (Test-Path $known) { return $known }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'No .NET SDK found. Pass -DotnetPath, set GLASSDESK_DOTNET, or install the SDK.'
}

function Resolve-InnoCompiler {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "ISCC.exe not found at '$Explicit'." }
        return $Explicit
    }
    foreach ($candidate in @(
            'C:\Program Files\Inno Setup 7\ISCC.exe',
            'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe',
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')) {
        if (Test-Path $candidate) { return $candidate }
    }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'ISCC.exe not found. Install Inno Setup or pass -InnoCompiler.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not x.y.z (optionally with a suffix)."
}

$dotnet = Resolve-Dotnet -Explicit $DotnetPath
$iscc = Resolve-InnoCompiler -Explicit $InnoCompiler
if (-not $StageDirectory) {
    $StageDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('glassdesk-stage-' + [guid]::NewGuid().ToString('N'))
}
$StageDirectory = [System.IO.Path]::GetFullPath($StageDirectory)
$issPath = Join-Path $repoRoot 'packaging\GlassDesk-Capture.iss'
$outputDirectory = Join-Path $repoRoot 'packaging\Output'

Write-Host "dotnet : $dotnet"
Write-Host "iscc   : $iscc"
Write-Host "version: $Version"
Write-Host "stage  : $StageDirectory"

Push-Location $repoRoot
try {
    if (Test-Path $StageDirectory) { Remove-Item $StageDirectory -Recurse -Force }

    Write-Host ''
    Write-Host '== self-contained publish ==' -ForegroundColor Cyan
    & $dotnet publish 'src/GlassDesk.App/GlassDesk.App.csproj' -c $Configuration -r win-x64 `
        --self-contained true -p:PublishSingleFile=false -o $StageDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE." }

    # The .iss references these; fail early with a clear message instead of a compiler error.
    foreach ($required in @('GlassDesk.exe', 'GlassDesk.ico', 'GlassDesk.runtimeconfig.json')) {
        if (-not (Test-Path (Join-Path $StageDirectory $required))) {
            throw "Staging directory is missing '$required'."
        }
    }
    $runtimeConfig = Get-Content (Join-Path $StageDirectory 'GlassDesk.runtimeconfig.json') -Raw
    if ($runtimeConfig -notmatch 'includedFrameworks') {
        throw 'Staging output is framework-dependent; the installer must ship a self-contained build.'
    }

    Write-Host ''
    Write-Host '== compile installer ==' -ForegroundColor Cyan
    $env:GLASSDESK_PACKAGE_STAGE = $StageDirectory
    # The asset name is a contract with UpdateClient.ParseRelease, which looks for exactly
    # "GlassDesk-Plus-<version>-win-x64-Setup.exe"; a different name makes the in-app update
    # check report that the release has no installer.
    $baseName = "GlassDesk-Plus-$Version-win-x64"
    # ISCC writes benign warnings to stderr; with ErrorActionPreference=Stop that alone would
    # abort this script, so relax it for the native call and judge by exit code instead.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $iscc `
            "/DAppIdValue=$AppId" `
            "/DProductVersion=$Version" `
            "/DFileVersion=$Version.0" `
            "/DPackageBaseName=$baseName" `
            $issPath
        $isccExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($isccExit -ne 0) { throw "ISCC failed with exit code $isccExit." }

    $setup = Join-Path $outputDirectory "$baseName-Setup.exe"
    if (-not (Test-Path $setup)) { throw "Installer was not produced at '$setup'." }

    Write-Host ''
    Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
    Write-Host 'Validate it by running it in an ordinary desktop session; not from a restricted token.'
}
finally {
    Pop-Location
}
