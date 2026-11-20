# Removes Alliance Status for the current user (also run from Settings > Apps).
$ErrorActionPreference = 'SilentlyContinue'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\AllianceStatus'
$exe = Join-Path $installDir 'AllianceStatus.exe'

Get-Process AllianceStatus | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $exe) { Start-Process $exe -ArgumentList '--cleanup' -Wait }

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AllianceStatus'
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AllianceStatus' -Recurse -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Alliance Status.lnk') -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'AllianceStatus') -Recurse -Force
Remove-Item $installDir -Recurse -Force
