# Baut das Paket. Pi: linux-arm64 für 64 Bit, linux-arm für 32 Bit (uname -m). PC: win-x64.
param([ValidateSet('linux-arm64', 'linux-arm', 'win-x64')][string]$Runtime = 'linux-arm64')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\skx-solar-$Runtime"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'SKx.Solar\SKx.Solar.csproj') -c Release -r $Runtime --self-contained -o (Join-Path $out 'app')
if ($LASTEXITCODE -ne 0) { throw 'Publish fehlgeschlagen.' }

if ($Runtime -eq 'win-x64') {
    Copy-Item (Join-Path $PSScriptRoot 'start-windows.cmd') (Join-Path $out 'app')
    Write-Host "Paket liegt in $out\app, Start mit start-windows.cmd"
    return
}

Copy-Item (Join-Path $PSScriptRoot 'install.sh'),(Join-Path $PSScriptRoot 'setup-nas-backup.sh'),(Join-Path $PSScriptRoot 'setup-https.sh'),(Join-Path $PSScriptRoot 'restore.sh'),(Join-Path $PSScriptRoot 'skx-solar-update'),(Join-Path $PSScriptRoot 'skx-solar.service') $out
# Der Pi erwartet Unix-Zeilenenden, Git unter Windows könnte sie sonst umstellen.
foreach ($name in 'install.sh', 'setup-nas-backup.sh', 'setup-https.sh', 'restore.sh', 'skx-solar-update', 'skx-solar.service') {
    $file = Join-Path $out $name
    [IO.File]::WriteAllText($file, ([IO.File]::ReadAllText($file) -replace "`r`n", "`n"))
}
Write-Host "Paket liegt in $out"
