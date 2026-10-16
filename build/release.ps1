# Publishes a GlassDesk Plus release to GitHub.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.
#
# Run this from a session that can actually reach github.com. A resolver that answers
# github.com with 127.0.0.1 (some campus/office DNS filters do) makes every push and asset
# upload fail; check with:
#   (Resolve-DnsName github.com -Server 223.5.5.5).IPAddress
#
# Steps: package -> commit -> tag -> push -> create release -> upload installer.
# The asset name is a contract with UpdateClient.ParseRelease, which looks for exactly
# "GlassDesk-Plus-<version>-win-x64-Setup.exe". Do not rename it.
#
# Authentication: set GITHUB_TOKEN (needs "repo" scope). Without it the script still pushes
# (git's own credential helper) but cannot create the release.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repository = 'Li-Jiayang85/GlassDesk-Plus',
    [string]$ReleaseTitle,
    [string]$NotesFile,
    [string]$Configuration = 'Release',
    [switch]$SkipPackage,
    [switch]$SkipPush,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
if (-not $ReleaseTitle) { $ReleaseTitle = "GlassDesk Plus $Version" }
if (-not $NotesFile) { $NotesFile = Join-Path $repoRoot "docs/releases/$Version.md" }
$installerName = "GlassDesk-Plus-$Version-win-x64-Setup.exe"
$installerPath = Join-Path $repoRoot "packaging/Output/$installerName"

function Invoke-Git {
    param([string[]]$Arguments)
    & git -C $repoRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

Push-Location $repoRoot
try {
    Write-Host "repository: $Repository"
    Write-Host "version   : $Version (tag $tag)"

    if (-not $SkipPackage) {
        Write-Host ''
        Write-Host '== package ==' -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'package.ps1') -Version $Version -Configuration $Configuration
    }
    if (-not (Test-Path $installerPath)) {
        throw "Installer not found at '$installerPath'. Run without -SkipPackage, or build it first."
    }
    Write-Host ("installer : {0} ({1:N1} MB)" -f $installerPath, ((Get-Item $installerPath).Length / 1MB))

    Write-Host ''
    Write-Host '== commit ==' -ForegroundColor Cyan
    Invoke-Git @('add', '-A')
    $pending = (& git -C $repoRoot status --porcelain) -join "`n"
    if ($pending) {
        Invoke-Git @('commit', '-m', "$ReleaseTitle")
        Write-Host 'committed working tree changes.'
    }
    else {
        Write-Host 'nothing to commit.'
    }

    Write-Host ''
    Write-Host '== tag ==' -ForegroundColor Cyan
    $existing = & git -C $repoRoot tag --list $tag
    if (-not $existing) {
        Invoke-Git @('tag', '-a', $tag, '-m', $ReleaseTitle)
        Write-Host "created tag $tag."
    }
    else {
        Write-Host "tag $tag already exists."
    }

    if (-not $SkipPush) {
        Write-Host ''
        Write-Host '== push ==' -ForegroundColor Cyan
        Invoke-Git @('push', 'origin', 'HEAD')
        Invoke-Git @('push', 'origin', $tag)
    }

    Write-Host ''
    Write-Host '== release ==' -ForegroundColor Cyan
    if (-not $env:GITHUB_TOKEN) {
        Write-Warning 'GITHUB_TOKEN is not set; skipping release creation.'
        Write-Host "Push succeeded. Create the release manually with the asset:"
        Write-Host "  $installerPath"
        return
    }

    $headers = @{
        Authorization          = "Bearer $($env:GITHUB_TOKEN)"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'glassdesk-release'
    }
    $bodyText = if (Test-Path $NotesFile) { Get-Content $NotesFile -Raw -Encoding UTF8 } else { $ReleaseTitle }
    $payload = @{
        tag_name         = $tag
        name             = $ReleaseTitle
        body             = $bodyText
        draft            = [bool]$Draft
        prerelease       = $false
        target_commitish = 'main'
    } | ConvertTo-Json -Depth 4

    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repository/releases" `
        -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
    Write-Host "release created: $($release.html_url)"

    $uploadUrl = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$installerName"
    Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $headers `
        -ContentType 'application/octet-stream' -InFile $installerPath | Out-Null
    Write-Host "asset uploaded: $installerName"
    Write-Host 'done.'
}
finally {
    Pop-Location
}
