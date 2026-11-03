#!/bin/sh
# Lädt das neueste Release und installiert oder aktualisiert SKx Solar. Aufruf auf dem Pi:
#   curl -fsSL https://raw.githubusercontent.com/yksmai/SKx.Solar/main/deploy/get.sh | sudo sh
# Ein Fork setzt SKX_SOLAR_REPO=besitzer/name (dann sudo -E oder sudo env ...).
set -eu

repo="${SKX_SOLAR_REPO:-yksmai/SKx.Solar}"

[ "$(id -u)" -eq 0 ] || { echo "Bitte mit sudo starten." >&2; exit 1; }

case "$(uname -m)" in
    aarch64|arm64) runtime=linux-arm64 ;;
    armv7l) runtime=linux-arm ;;
    # .NET läuft nicht auf ARMv6 (Pi 1, Zero der ersten Generation).
    *) echo "Nicht unterstützt: $(uname -m). Benötigt wird ein Pi mit 64-Bit- oder ARMv7-System." >&2; exit 1 ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

url="https://github.com/$repo/releases/latest/download/skx-solar-$runtime.tar.gz"
echo "Lade $url"
curl -fL --retry 3 -o "$work/skx-solar.tar.gz" "$url"
tar xzf "$work/skx-solar.tar.gz" --no-same-owner -C "$work"

sh "$work/skx-solar-$runtime/install.sh"
