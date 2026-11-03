# Baut die App und startet den Spiegel auf dem Server neu. Daten und Token bleiben dort erhalten.
param(
    [string]$ServerHost = $env:SKX_SOLAR_MIRROR_HOST,
    [string]$User = 'root'
)

$ErrorActionPreference = 'Stop'
if (-not $ServerHost) { throw 'Server fehlt: -ServerHost angeben oder SKX_SOLAR_MIRROR_HOST setzen.' }
$root = Split-Path $PSScriptRoot -Parent
$target = "$User@$ServerHost"
$out = Join-Path $root 'artifacts\mirror'

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root 'SKx.Solar\SKx.Solar.csproj') -c Release -r linux-x64 --self-contained false -o (Join-Path $out 'app') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Bauen fehlgeschlagen.' }
Copy-Item (Join-Path $PSScriptRoot 'mirror\Dockerfile'), (Join-Path $PSScriptRoot 'mirror\docker-compose.yml') $out

Push-Location $root
try {
    tar -C artifacts/mirror -czf artifacts/mirror.tgz .
    if ($LASTEXITCODE -ne 0) { throw 'Packen fehlgeschlagen.' }
}
finally {
    Pop-Location
}
scp -o BatchMode=yes (Join-Path $root 'artifacts\mirror.tgz') "${target}:/root/solar-mirror.tgz"
if ($LASTEXITCODE -ne 0) { throw 'Übertragung fehlgeschlagen.' }

$script = 'set -e; mkdir -p /root/solar; cd /root/solar; rm -rf app; tar -xzf /root/solar-mirror.tgz; rm /root/solar-mirror.tgz; ' +
    '[ -f .env ] || { umask 077; echo "SOLAR_MIRROR_TOKEN=$(openssl rand -hex 32)" > .env; }; ' +
    'docker compose up -d --build'
ssh -o BatchMode=yes $target $script
if ($LASTEXITCODE -ne 0) { throw 'Start auf dem Server fehlgeschlagen.' }
Write-Host "Spiegel läuft auf $ServerHost"
