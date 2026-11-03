#!/bin/bash
# Stellt das Dashboard per Caddy unter HTTPS bereit, mit eigener lokaler Zertifizierungsstelle.
# Aufruf: sudo bash setup-https.sh. Nach einem Wechsel der IP erneut ausführen.
set -eu

[ "$(id -u)" -eq 0 ] || { echo "Bitte mit sudo starten." >&2; exit 1; }

dpkg -s caddy >/dev/null 2>&1 || apt-get install -y caddy

host="$(hostname)"
addresses="https://$host.fritz.box, https://$host.local"
for ip in $(hostname -I); do
    case "$ip" in
        *:*) ;;
        *) addresses="$addresses, https://$ip" ;;
    esac
done

cat > /etc/caddy/Caddyfile <<EOF
{
    auto_https disable_redirects
}

$addresses {
    tls internal
    reverse_proxy localhost:8080
}

# Unverschlüsselt gibt es nur das Stammzertifikat, damit Geräte ihm vertrauen können.
http:// {
    handle /skx-root.crt {
        root * /etc/caddy
        file_server
    }
    handle {
        redir https://{host}{uri}
    }
}
EOF

systemctl enable caddy
systemctl restart caddy

# Caddy legt die Zertifizierungsstelle beim ersten Start an.
root=/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt
for _ in 1 2 3 4 5 6 7 8 9 10; do
    [ -f "$root" ] && break
    sleep 1
done
[ -f "$root" ] || { echo "Stammzertifikat wurde nicht erzeugt." >&2; exit 1; }
install -m 644 "$root" /etc/caddy/skx-root.crt

first="$(hostname -I | awk '{print $1}')"
echo "Fertig. Dashboard: https://$first"
echo "Stammzertifikat für Handy und PC: http://$first/skx-root.crt"
