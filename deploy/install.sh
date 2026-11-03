#!/bin/sh
# Installiert oder aktualisiert SKx Solar. Aufruf auf dem Pi im entpackten Ordner: sudo sh install.sh
set -eu

[ "$(id -u)" -eq 0 ] || { echo "Bitte mit sudo starten." >&2; exit 1; }

here="$(cd "$(dirname "$0")" && pwd)"

id skx-solar >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin skx-solar
install -d -o skx-solar -g skx-solar /var/lib/skx-solar
install -d /opt/skx-solar

systemctl stop skx-solar 2>/dev/null || true

# Lokale Einstellungen (Preise, IP) bei einem Update behalten.
if [ -f /opt/skx-solar/appsettings.json ]; then
    cp /opt/skx-solar/appsettings.json /tmp/skx-solar-appsettings.json
fi
cp -r "$here"/app/. /opt/skx-solar/
if [ -f /tmp/skx-solar-appsettings.json ]; then
    mv /tmp/skx-solar-appsettings.json /opt/skx-solar/appsettings.json
fi
chmod +x /opt/skx-solar/SKx.Solar

install -m 644 "$here/skx-solar.service" /etc/systemd/system/skx-solar.service

# Systemprotokolle nur im RAM halten, das schont die SD-Karte.
install -d /etc/systemd/journald.conf.d
printf '[Journal]\nStorage=volatile\nRuntimeMaxUse=32M\n' > /etc/systemd/journald.conf.d/skx-solar.conf
systemctl restart systemd-journald

# Startet den Pi neu, wenn das gesamte System hängt.
install -d /etc/systemd/system.conf.d
printf '[Manager]\nRuntimeWatchdogSec=30s\n' > /etc/systemd/system.conf.d/skx-solar.conf
systemctl daemon-reexec

# Updates ohne Passwort: Ein neues Archiv im Feed-Ordner startet das Einspielen.
owner="${SUDO_USER:-pi}"
feed="$(getent passwd "$owner" | cut -d: -f6)/skx-solar-feed"
install -d -o "$owner" -g "$owner" "$feed"
install -m 755 "$here/skx-solar-update" /usr/local/sbin/skx-solar-update
printf '[Unit]\nDescription=SKx Solar Update\n\n[Service]\nType=oneshot\nExecStart=/usr/local/sbin/skx-solar-update %s/skx-solar-update.tgz\n' "$feed" > /etc/systemd/system/skx-solar-update.service
printf '[Unit]\nDescription=SKx Solar wartet auf Updates\n\n[Path]\nPathChanged=%s/skx-solar-update.tgz\n\n[Install]\nWantedBy=multi-user.target\n' "$feed" > /etc/systemd/system/skx-solar-update.path

systemctl daemon-reload
systemctl enable --now skx-solar
systemctl enable --now skx-solar-update.path

echo "Fertig. Dashboard: http://$(hostname -I | awk '{print $1}'):8080"
