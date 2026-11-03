# Baut das Update-Archiv für den Pi. update-pi.ps1 oder ein eigenes Deploy-Werkzeug lädt es danach in den Feed-Ordner, der Pi spielt es selbst ein.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$feed = Join-Path $root 'artifacts\pi-feed'

& (Join-Path $PSScriptRoot 'publish.ps1') -Runtime linux-arm64
New-Item -ItemType Directory -Force $feed | Out-Null
# Relative Pfade: tar hält den Doppelpunkt in C:\ sonst je nach Variante für einen entfernten Rechner.
Push-Location $root
try {
    tar -C artifacts/skx-solar-linux-arm64 -czf artifacts/pi-feed/skx-solar-update.tgz app
    if ($LASTEXITCODE -ne 0) { throw 'Packen fehlgeschlagen.' }
}
finally {
    Pop-Location
}
Write-Host "Archiv liegt in $feed"
