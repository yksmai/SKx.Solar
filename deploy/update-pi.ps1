# Baut die App und spielt sie per SSH auf den Pi. Erwartet einmal ausgeführtes install.sh und einen SSH-Schlüssel.
param(
    # Fester Wert per Umgebungsvariable, damit keine Adresse im Repository steht.
    [string]$PiHost = $(if ($env:SKX_SOLAR_PI_HOST) { $env:SKX_SOLAR_PI_HOST } else { 'raspberrypi.local' }),
    [string]$User = 'pi'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = "$User@$PiHost"

& (Join-Path $PSScriptRoot 'build-feed.ps1')

# Erst vollständig hochladen, dann umbenennen: Der Pi reagiert nur auf das fertige Archiv.
scp -o BatchMode=yes (Join-Path $root 'artifacts\pi-feed\skx-solar-update.tgz') "${target}:skx-solar-feed/.incoming.tmp"
if ($LASTEXITCODE -ne 0) { throw 'Übertragung fehlgeschlagen.' }
ssh -o BatchMode=yes $target 'mv ~/skx-solar-feed/.incoming.tmp ~/skx-solar-feed/skx-solar-update.tgz'
if ($LASTEXITCODE -ne 0) { throw 'Übertragung fehlgeschlagen.' }

# Der Dienst braucht einen Moment, bis die ersten Messwerte vorliegen.
foreach ($attempt in 1..18) {
    Start-Sleep -Seconds 5
    $state = ssh -o BatchMode=yes $target 'systemctl is-active skx-solar-update.service; curl -sf -m 4 http://localhost:8080/health'
    if ($state -match 'inactive' -and $state -match 'ok') {
        Write-Host "Läuft: http://${PiHost}:8080"
        return
    }
}
throw 'Der Dienst meldet nach anderthalb Minuten keine aktuellen Daten.'
