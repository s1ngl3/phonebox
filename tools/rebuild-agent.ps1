$ErrorActionPreference = 'Stop'
$root = 'D:\PhoneBox'
$app = Join-Path $env:LOCALAPPDATA 'PhoneBox\app'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-publish-' + [guid]::NewGuid().ToString('N'))

Write-Host '==> Publishing agent'
dotnet publish (Join-Path $root 'agent\PhoneBox.Agent') -c Release -f net10.0-windows10.0.19041.0 -o $stage --nologo -v q --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

Write-Host '==> Stopping running agent'
Get-Process PhoneBoxAgent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host '==> Installing to app dir'
New-Item -ItemType Directory -Path $app -Force | Out-Null
foreach ($entry in (Get-ChildItem -LiteralPath $stage -Force)) {
    Copy-Item -LiteralPath $entry.FullName -Destination $app -Recurse -Force
}
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '==> Starting agent on desktop'
wmic process call create "$env:LOCALAPPDATA\PhoneBox\app\PhoneBoxAgent.exe"
Start-Sleep -Seconds 2
Get-Process PhoneBoxAgent -ErrorAction SilentlyContinue | Select-Object Id, ProcessName
