$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'GlassDeskPlusPatch'
New-Item -ItemType Directory -Force -Path $target | Out-Null

$dll = Join-Path $PSScriptRoot 'GlassDeskPatch.dll'
if (-not (Test-Path $dll)) {
    $built = Join-Path $root 'src\bin\Release\net10.0-windows\GlassDeskPatch.dll'
    if (Test-Path $built) { $dll = $built } else { throw 'GlassDeskPatch.dll not found. Build src/ first.' }
}
Copy-Item $dll (Join-Path $target 'GlassDeskPatch.dll') -Force
Copy-Item (Join-Path $root 'assets\wechat.jpg') (Join-Path $target 'wechat.jpg') -Force
Copy-Item (Join-Path $root 'assets\alipay.jpg') (Join-Path $target 'alipay.jpg') -Force
[Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', (Join-Path $target 'GlassDeskPatch.dll'), 'User')
Write-Host "Installed to $target"
Write-Host 'Restart GlassDesk to apply.'