$target = Join-Path $env:LOCALAPPDATA 'GlassDeskPlusPatch'
[Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $null, 'User')
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Write-Host 'Removed GlassDesk patch. Restart GlassDesk to apply.'