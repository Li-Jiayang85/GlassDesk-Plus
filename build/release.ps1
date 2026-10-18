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
    # Where the GitHub release (and its installer asset) is published. Defaults to -Repository.
    #
    # Do NOT point this at the upstream author's repository (everlosttime/glassdesk-updates).
    # That repo is not ours: we have no write access, and the in-app update source that led
    # there is a leftover update-source.json inherited from the upstream product. The right
    # fix for an install whose check fails with "no unique Windows x64 installer" is to delete
    # that file, not to publish into someone else's repository.
    [string]$ReleaseRepository,
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
if (-not $ReleaseRepository) { $ReleaseRepository = $Repository }
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
    # 必须显式 [string]：Get-Content -Raw 返回的字符串带 ETS 属性（PSPath/PSParentPath…），
    # PowerShell 5.1 的 ConvertTo-Json 会把它当成对象序列化，body 会变成 {"value": …} 这样的
    # 垃圾，GitHub 上显示出来就是一串哈希表文本而不是 Markdown。
    $bodyText = if (Test-Path $NotesFile) { [string](Get-Content $NotesFile -Raw -Encoding UTF8) } else { $ReleaseTitle }
    Write-Host "release repo: $ReleaseRepository"

    $releaseUrl = "https://api.github.com/repos/$ReleaseRepository/releases"
    $payloadPath = Join-Path ([System.IO.Path]::GetTempPath()) ("glassdesk-release-" + [guid]::NewGuid().ToString('N') + ".json")
    [System.IO.File]::WriteAllText($payloadPath, (@{
        tag_name         = $tag
        name             = $ReleaseTitle
        body             = $bodyText
        draft            = [bool]$Draft
        prerelease       = $false
        target_commitish = 'main'
    } | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))

    $release = $null
    try {
        $release = Invoke-RestMethod -Method Post -Uri $releaseUrl -Headers $headers `
            -ContentType 'application/json' -InFile $payloadPath
    }
    catch {
        # Some filtering proxies reject a non-ASCII body on POST with 422 while accepting the
        # same text on PATCH. Create with an ASCII placeholder, then patch the real notes in.
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -ne 422) { throw }
        Write-Warning "POST rejected the notes body (HTTP 422); creating with a placeholder and patching them in."
        [System.IO.File]::WriteAllText($payloadPath, (@{
            tag_name         = $tag
            name             = $ReleaseTitle
            body             = $ReleaseTitle
            draft            = [bool]$Draft
            prerelease       = $false
            target_commitish = 'main'
        } | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
        $release = Invoke-RestMethod -Method Post -Uri $releaseUrl -Headers $headers `
            -ContentType 'application/json' -InFile $payloadPath

        [System.IO.File]::WriteAllText($payloadPath, (@{
            body = $bodyText
        } | ConvertTo-Json -Depth 3), (New-Object System.Text.UTF8Encoding($false)))
        $release = Invoke-RestMethod -Method Patch -Uri "$releaseUrl/$($release.id)" -Headers $headers `
            -ContentType 'application/json' -InFile $payloadPath
        Write-Host 'notes patched in.'
    }
    finally { Remove-Item $payloadPath -Force -ErrorAction SilentlyContinue }
    Write-Host "release created: $($release.html_url)"

    # Upload with curl: schannel cannot complete the revocation check behind some proxies
    # (CRYPT_E_NO_REVOCATION_CHECK), which fails the upload outright.
    #
    # 上传需要 uploads.github.com。某些网络会把它解析到 127.0.0.1（DNS 拦截），
    # 或者连上了却中途断开，留下一个 starter 残包。这里按「直连 → 公共 DNS 地址逐个
    # --resolve」的顺序重试，并在每次重试前删掉残包，免得同名冲突。
    $curl = Join-Path $env:SystemRoot 'System32/curl.exe'
    $uploadUrl = "https://uploads.github.com/repos/$ReleaseRepository/releases/$($release.id)/assets?name=$installerName"
    $asset = $null
    $targets = @('')
    foreach ($server in @('223.5.5.5', '8.8.8.8')) {
        $found = @()
        try {
            $found = @(Resolve-DnsName uploads.github.com -Server $server -Type A -QuickTimeout -ErrorAction Stop |
                Where-Object IPAddress | Select-Object -ExpandProperty IPAddress -Unique)
        } catch {
            $found = @()
        }
        foreach ($address in $found) {
            if ($address -and $address -notlike '127.*' -and $targets -notcontains $address) { $targets += $address }
        }
    }

    foreach ($address in $targets) {
        $extra = @()
        if ($address) {
            $extra = @('--resolve', "uploads.github.com:443:$address")
            Write-Host "uploading via $address"
        }
        $raw = & $curl -sS @extra --ssl-no-revoke --http1.1 --connect-timeout 20 --max-time 900 `
            -X POST -H "Authorization: Bearer $($env:GITHUB_TOKEN)" `
            -H 'Accept: application/vnd.github+json' `
            -H 'Content-Type: application/octet-stream' `
            -H 'User-Agent: glassdesk-release' `
            -H 'Expect:' `
            --data-binary "@$installerPath" $uploadUrl 2>&1
        try { $asset = (($raw | Out-String).Trim() | ConvertFrom-Json) } catch { $asset = $null }
        if ($asset -and $asset.state -eq 'uploaded') { break }

        # 收拾残包：starter 状态的同名资产会让下一次上传撞名失败
        $existing = @(Invoke-RestMethod -Uri "https://api.github.com/repos/$ReleaseRepository/releases/$($release.id)/assets" -Headers $headers -TimeoutSec 30)
        foreach ($item in $existing) {
            if ($item.name -eq $installerName) {
                Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$ReleaseRepository/releases/assets/$($item.id)" -Headers $headers -TimeoutSec 30
            }
        }
    }

    if (-not $asset -or $asset.name -ne $installerName -or $asset.state -ne 'uploaded') {
        throw "asset upload failed: $(($asset | ConvertTo-Json -Compress -Depth 3))"
    }
    Write-Host "asset uploaded: $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB, $($asset.state), $($asset.digest))"
}
finally {
    Pop-Location
}
