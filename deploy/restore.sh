#!/bin/bash
# Spielt eine Sicherung zurück. Aufruf: sudo bash restore.sh /pfad/zu/solar-JJJJ-MM-TT.db
# Ohne Angabe nimmt das Skript die neueste Sicherung aus dem eingestellten Ziel.
set -eu

[ "$(id -u)" -eq 0 ] || { echo "Bitte mit sudo starten." >&2; exit 1; }

data=/var/lib/skx-solar
target=/mnt/skx-solar-backup
[ -d "$target" ] && ls "$target"/solar-*.db >/dev/null 2>&1 || target="$data/backups"

source="${1:-$(ls "$target"/solar-*.db 2>/dev/null | sort | tail -n 1)}"
[ -n "$source" ] && [ -f "$source" ] || { echo "Keine Sicherung gefunden." >&2; exit 1; }

echo "Stelle $source wieder her."
systemctl stop skx-solar

# Der bisherige Stand bleibt liegen, falls die Sicherung nicht die richtige war.
if [ -f "$data/solar.db" ]; then
    keep="$data/before-restore-$(date +%Y%m%d-%H%M%S)"
    install -d -o skx-solar -g skx-solar "$keep"
    mv "$data"/solar.db* "$keep"/
    echo "Bisheriger Stand: $keep"
fi

install -o skx-solar -g skx-solar -m 644 "$source" "$data/solar.db"
settings="$(dirname "$source")/solar-settings.json"
[ -f "$settings" ] && install -o skx-solar -g skx-solar -m 600 "$settings" "$data/settings.json"
auth="$(dirname "$source")/solar-auth.json"
[ -f "$auth" ] && install -o skx-solar -g skx-solar -m 600 "$auth" "$data/auth.json"

systemctl start skx-solar
echo "Fertig."
