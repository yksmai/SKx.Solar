#!/bin/bash
# Bindet eine SMB-Freigabe als Sicherungsziel ein. Aufruf nach install.sh: sudo bash setup-nas-backup.sh //NAS/Freigabe DSM-Benutzer
set -eu

[ "$(id -u)" -eq 0 ] || { echo "Bitte mit sudo starten." >&2; exit 1; }
[ $# -eq 2 ] || { echo "Aufruf: sudo bash $0 //NAS/Freigabe DSM-Benutzer" >&2; exit 1; }
id skx-solar >/dev/null 2>&1 || { echo "Erst install.sh ausführen." >&2; exit 1; }

share="$1"
user="$2"
target=/mnt/skx-solar-backup
credentials=/etc/skx-solar-nas.credentials

dpkg -s cifs-utils >/dev/null 2>&1 || apt-get install -y cifs-utils

ask_password() {
    IFS= read -r -s -p "Passwort für $user: " password
    echo
    # Nur root darf das Passwort lesen.
    install -m 600 /dev/null "$credentials"
    printf 'username=%s\npassword=%s\n' "$user" "$password" > "$credentials"
    unset password
}

# Ein früher gespeichertes Passwort weiterverwenden, solange das NAS es annimmt.
if [ ! -f "$credentials" ] || ! grep -q "^username=$user$" "$credentials"; then
    ask_password
fi

install -d "$target"
automount="$(systemd-escape -p --suffix=automount "$target")"
systemctl stop "$automount" 2>/dev/null || true
umount "$target" 2>/dev/null || true

# Ältere NAS sprechen kein aktuelles SMB, darum die neueste Version suchen, die beide können. SMB1 bleibt aus.
base="credentials=$credentials,uid=skx-solar,gid=skx-solar,file_mode=0640,dir_mode=0750,nobrl"
version=""
for attempt in 1 2 3; do
    problem=""
    for candidate in 3.1.1 3.0 2.1 2.0; do
        if output="$(mount -t cifs "$share" "$target" -o "$base,vers=$candidate" 2>&1)"; then
            version="$candidate"
            umount "$target"
            break
        fi
        case "$output" in
            *"error(95)"*) problem="Das NAS spricht keine SMB-Version ab 2.0. In DSM unter Dateidienste > SMB das höchste Protokoll auf SMB3 stellen." ;;
            *"error(13)"*) problem=login; break ;;
            *"error(2)"*) problem="Die Freigabe $share gibt es nicht. Den Namen in DSM unter Gemeinsamer Ordner prüfen."; break ;;
            *) problem="$output"; break ;;
        esac
    done
    [ -z "$version" ] || break
    [ "$problem" = login ] || { echo "$problem" >&2; exit 1; }

    # Auch der Kernel nennt den Grund: falsche Anmeldung oder fehlendes Recht auf den Ordner.
    if dmesg | grep CIFS | tail -n 3 | grep -q STATUS_LOGON_FAILURE; then
        echo "Das NAS lehnt Benutzer oder Passwort ab. Der DSM-Benutzer $user muss existieren und aktiv sein." >&2
    else
        echo "Anmeldung abgelehnt. Prüfen, ob $user in DSM aktiv ist und Lese-/Schreibrecht auf den Ordner sowie die Anwendung SMB hat." >&2
    fi
    # Ein abgelehntes Passwort darf nicht liegen bleiben, sonst fragt der nächste Lauf nicht mehr.
    rm -f "$credentials"
    [ "$attempt" -lt 3 ] || exit 1
    ask_password
done
echo "Verbunden mit SMB $version."

# Automount: Ein ausgeschaltetes NAS blockiert den Start des Pi nicht.
options="$base,vers=$version,_netdev,nofail,x-systemd.automount,x-systemd.idle-timeout=600"
{ grep -v " $target " /etc/fstab || true; echo "$share $target cifs $options 0 0"; } > /etc/fstab.skx-solar
mv /etc/fstab.skx-solar /etc/fstab
systemctl daemon-reload
systemctl restart "$automount"

probe="$target/.skx-solar-test"
sudo -u skx-solar touch "$probe"
rm -f "$probe"

install -d /etc/systemd/system/skx-solar.service.d
printf '[Service]\nEnvironment=Solar__Backup__Directory=%s\n' "$target" > /etc/systemd/system/skx-solar.service.d/backup.conf
systemctl daemon-reload
systemctl restart skx-solar

echo "Fertig. Sicherungen landen auf $share."
