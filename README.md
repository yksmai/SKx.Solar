# SKx Solar

Sammelt die Daten eines Deye-Hybrid-Wechselrichters über den Datenlogger (Solarman V5, TCP 8899), speichert sie in SQLite und zeigt sie als Web-Dashboard. Läuft ohne Cloud und ohne App. Der Client beherrscht nur Modbus-Funktion 03 (lesen), am Wechselrichter lässt sich damit nichts verstellen.

## Funktionen

| Bereich | Inhalt | Braucht |
|---|---|---|
| Übersicht | Energiefluss live, Tageswerte, Verlauf Tag/Monat/Jahr, Bilanz, Anlagendetails | nichts |
| Analyse | Batterie, Ladestand-Verteilung, Jahresvergleich, beste Tage, Strings, Ertragskalender | für Zyklen `BatteryCapacityKwh` |
| Planung | Ertragsprognose, Strompreise, Wirtschaftlichkeit | jeweils eigene Einstellung |
| Meldungen | Verlauf von Störungen, Ausfällen, Batterie, Temperatur, Ertrag; optional aufs Handy | für Versand ntfy oder Telegram |
| System | Dienste, Speicher, Sicherungen, Exporte | nichts |

Ohne Einstellung bleibt jede Erweiterung aus, und es verlässt nichts das Heimnetz.

## Installation auf dem Raspberry Pi

Der Pi braucht ein aktuelles Raspberry Pi OS Lite, gern 64 Bit (`uname -m` zeigt `aarch64`). .NET muss nicht installiert sein.

Auf dem PC bauen (für 32 Bit `-Runtime linux-arm`):

```powershell
./deploy/publish.ps1 -Runtime linux-arm64
```

Paket kopieren und installieren:

```bash
scp -r artifacts/skx-solar-linux-arm64 pi@<ip-des-pi>:~/skx-solar
ssh pi@<ip-des-pi> "cd ~/skx-solar && sudo sh install.sh"
```

Das Dashboard läuft danach unter `http://<ip-des-pi>:8080`. Beim ersten Aufruf legst du das Adminkonto an, danach unter **Planung → Einstellungen → Wechselrichter** die Adresse des Loggers eintragen. Die Seriennummer kann leer bleiben, dann sucht die App den Logger selbst. Ein Update funktioniert genauso und behält die Einstellungen in `/opt/skx-solar/appsettings.json`. Die Daten liegen in `/var/lib/skx-solar/solar.db`.

```bash
systemctl status skx-solar
journalctl -u skx-solar -f
```

## Auf dem PC mit direktem Ersatzweg

Die App läuft auch auf dem PC und zeigt dort die Daten des Pi. Antwortet er nicht oder hat selbst keine aktuellen Werte, liest sie nach spätestens rund 20 Sekunden direkt vom Wechselrichter und schaltet zurück, sobald der Pi wieder da ist.

```powershell
./deploy/publish.ps1 -Runtime win-x64
```

In `artifacts/skx-solar-win-x64/app/appsettings.json` die Adresse des Pi eintragen und `start-windows.cmd` starten:

```json
"Upstream": { "Url": "http://<ip-des-pi>:8080" }
```

Im Direktbetrieb zeichnet der PC nur auf, solange er läuft, in seine eigene Datenbank. Diese Werte fließen nicht in die Datenbank des Pi; dessen Tagessummen stimmen trotzdem, weil sie aus den Gesamtzählern entstehen. Benachrichtigungen, MQTT und Prognose bitte nur auf dem Pi einrichten, sonst laufen sie doppelt. Der Weg funktioniert nur im selben Netz wie der Logger. Handy und Browser allein können den Logger nicht ansprechen, weil Webseiten keine reinen TCP-Verbindungen öffnen dürfen.

## Betrieb auf dem Pi

- **Erste Anmeldung:** Das Dashboard fragt beim ersten Aufruf nach Benutzername und Passwort für das Adminkonto. Weitere Konten (Admin oder Nur lesen) legst du unter **System → Benutzer** an. Alle Passwörter vergessen? `sudo rm /var/lib/skx-solar/auth.json && sudo systemctl restart skx-solar`, dann legt der nächste Aufruf ein neues Adminkonto an.
- **Update vom PC:** `deploy/update-pi.ps1 -PiHost <ip-des-pi>` (oder Umgebungsvariable `SKX_SOLAR_PI_HOST`) legt ein neues Archiv nach `~/skx-solar-feed`. Der Pi erkennt es über `skx-solar-update.path` und spielt es selbst ein, dafür braucht es kein `sudo`.
- **SD-Karte:** `SdCardMode` schreibt alle 5 Minuten und hält einen Rohwert pro Minute für 14 Tage. `install.sh` legt die Systemprotokolle in den RAM, sie gehen beim Neustart verloren.
- **Watchdog:** systemd startet den Dienst neu, wenn die Datensammlung 5 Minuten stillsteht, und der Hardware-Watchdog den Pi, wenn das System hängt.
- **Sicherung zurückspielen:** `sudo bash restore.sh [datei]` nimmt ohne Angabe die neueste. Der bisherige Stand bleibt unter `/var/lib/skx-solar/before-restore-*`.
- **HTTPS:** `sudo bash setup-https.sh` richtet Caddy mit eigener Zertifizierungsstelle ein. Für die Installation als Handy-App das Stammzertifikat von `http://<ip>/skx-root.crt` auf dem Gerät als vertrauenswürdig einrichten. Nach einem IP-Wechsel erneut ausführen.
- **Home Assistant:** Mit eingetragenem MQTT-Broker erscheinen die Sensoren über MQTT-Discovery von selbst.

## Einstellungen

Wechselrichter-Adresse, Batteriekapazität, Investition, Balkonkraftwerk, Tarife, Prognose, Börsenpreise, Benachrichtigungen, MQTT, Spiegel-Server, Datensicherung und Kartenschonung stellst du im Dashboard unter **Planung** ein, Benutzer unter **System**. Sie gelten sofort, liegen in `settings.json` und `auth.json` neben der Datenbank, haben Vorrang vor `appsettings.json` und werden mitgesichert. Die Beispiele unten zeigen dieselben Werte für alle, die lieber die Datei pflegen.

Nur Abfrageintervall, Aufbewahrung der Rohwerte, Datenbankpfad, PC-Ersatzweg (`Upstream`) und Spiegelbetrieb (`Mirror`) stehen allein im Abschnitt `Solar` in `appsettings.json`, danach `sudo systemctl restart skx-solar`.

### Grundlagen

| Schlüssel | Bedeutung |
|---|---|
| `Host`, `Port` | Adresse des Loggers, auch im Dashboard. Am besten im Router eine feste IP vergeben. |
| `LoggerSerial` | Seriennummer des Loggers, leer = automatisch per UDP ermitteln |
| `PollSeconds` | Abfrageintervall, Standard 10 s |
| `RawRetentionDays` | Aufbewahrung der Rohwerte, Standard 30 Tage. Minuten- und Tageswerte bleiben dauerhaft. |
| `BatteryCapacityKwh` | Nutzbare Kapazität, für die Zyklenzahl |

### Wirtschaftlichkeit

```json
"InvestmentEur": 15000,
"Tariffs": [
  { "From": "2026-01-01", "ImportEurPerKwh": 0.32, "FeedInEurPerKwh": 0.08 }
]
```

Jeder Eintrag gilt ab `From`, bis ein neuerer beginnt. Tage vor dem ersten Eintrag zählen nicht zur Ersparnis. Die Amortisation rechnet erst ab 30 Messtagen hoch und bleibt ohne ein ganzes Jahr grob.

### Meldungen aufs Handy

```json
"Alerts": {
  "BatteryLowPercent": 15,
  "InverterTempLimitC": 75,
  "MinNotifySeverity": "warning",
  "NtfyTopic": "ein-langer-zufaelliger-name",
  "TelegramBotToken": null,
  "TelegramChatId": null
}
```

Geprüft werden: Gerätefehler und Fehlercodes, 5 Minuten ohne Daten, Batterie leer oder voll, Temperatur, ein String ohne Strom über 15 Minuten, schwacher Tagesertrag gegenüber den letzten 14 Tagen, fehlgeschlagene Sicherung. Topics auf `ntfy.sh` sind öffentlich lesbar, wenn jemand den Namen errät: langen Zufallsnamen wählen oder mit `NtfyServer` einen eigenen Server nutzen. Fehlercodes erscheinen roh als Hex, ihre Bedeutung steht im Handbuch des Geräts.

### Ertragsprognose

```json
"Forecast": {
  "Latitude": 52.5,
  "Longitude": 13.4,
  "PerformanceRatio": 0.8,
  "Planes": [
    { "TiltDegrees": 30, "AzimuthDegrees": 0, "Kwp": 5.0 },
    { "TiltDegrees": 30, "AzimuthDegrees": 90, "Kwp": 3.0 }
  ]
}
```

Azimut: 0 = Süd, -90 = Ost, 90 = West. Daten kommen stündlich von Open-Meteo, dorthin gehen die Koordinaten. Die kostenlose API ist für nicht kommerzielle Nutzung gedacht.

### Strompreise

```json
"DynamicPrices": { "Enabled": true, "Country": "de", "SurchargeCtPerKwh": 15.0, "VatPercent": 19 }
```

Börsenpreise von aWATTar für Deutschland oder Österreich. Ohne Aufschlag zeigt die Anzeige den reinen Börsenpreis. Die App zeigt die Preise nur an und steuert nichts.

### Überschuss per MQTT

```json
"Mqtt": { "Host": "192.168.1.10", "Port": 1883, "User": null, "Password": null, "Topic": "skx-solar/state" }
```

Veröffentlicht bei jeder Messung als Retained-Nachricht `pv_w`, `load_w`, `grid_w`, `battery_w`, `battery_soc`, `export_w`, `surplus_w` und `state`. `surplus_w` ist Einspeisung plus Batterieladung. Wallbox, Heizstab oder Home Assistant werten das selbst aus: Die App abonniert nichts, schaltet nichts und schreibt nicht auf den Wechselrichter.

### Datensicherung

```json
"Backup": { "Enabled": true, "Directory": null, "KeepDays": 14, "Hour": 3 }
```

Sichert täglich ab der genannten Stunde eine konsistente Kopie und löscht nur eigene Dateien `solar-JJJJ-MM-TT.db`, die älter sind. **Ohne `Directory` liegt die Sicherung auf derselben SD-Karte und schützt nicht vor deren Ausfall.** Für ein NAS die Freigabe nach `/mnt/skx-solar-backup` einhängen und `Directory` darauf setzen, der Dienst darf dorthin schreiben.

### Anmeldung

Konten mit den Rollen Admin und Nur lesen verwaltest du unter **System → Benutzer**. Nur lesende Konten sehen alle Auswertungen, aber keine Einstellungen, Benutzer oder den Datenbank-Snapshot. Ein älteres `Auth:PasswordHash` aus `appsettings.json` wird beim Start zum Konto `admin`.

Über reines HTTP läuft das Passwort unverschlüsselt durchs Heimnetz. Das Dashboard gehört deshalb auch mit Anmeldung nicht per Portfreigabe ins Internet; von außen besser per VPN (etwa WireGuard in der FRITZ!Box).

### Als App auf dem Handy

Die Seite bringt Manifest, Symbole und Offline-Oberfläche mit. Browser erlauben die vollständige Installation samt Service Worker nur über HTTPS. Über `http://<ip-des-pi>` klappt meist nur eine Verknüpfung auf dem Startbildschirm; für echtes HTTPS braucht es einen Reverse-Proxy wie Caddy.

### Spiegel-Server für unterwegs

Ein eigener Server zeigt dieselbe Oberfläche nur lesend an, der Pi schickt ihm seine Werte per HTTPS und Token. Adressen im Heimnetz, Zugangsdaten und Sicherungspfade verlassen den Pi dabei nicht. Den Server richtest du mit `deploy/mirror` (Docker Compose hinter einem Reverse Proxy) ein:

```powershell
./deploy/update-mirror.ps1 -ServerHost <server>
```

Beim ersten Start legt das Skript auf dem Server ein Token in `/root/solar/.env` an. Dieses Token und die Adresse des Servers trägst du auf dem Pi unter **Planung → Spiegel-Server** ein. In `.env` lassen sich außerdem `SOLAR_PROXY_NETWORK` (Docker-Netz des Proxys, Standard `app_default`) und `TZ` setzen.

## Dach, Balkon und Dachflächen

Im Verlauf lassen sich Gesamt, Dach und Balkon einzeln schalten; im Monat und Jahr stapeln sich Dach und Balkon, solange alle drei an sind. Die Auswahl merkt sich der Browser.

Dachflächen lassen sich wahlweise eingeben: Leistung in kWp oder als Module × Watt, Ausrichtung als Himmelsrichtung, Kompasswert oder Winkel zu Süd, Neigung als grobe Stufe oder genauer Wert. Unter Planung vergleicht „Passt die Dachausrichtung?“ an klaren Tagen den Erzeugungsschwerpunkt des Dachs mit der Eingabe.

## Daten und Analysen mit SKx

| Objekt | Inhalt |
|---|---|
| `v_SolarMinute` | Minutenwerte: Leistungen, Ladestand, Temperaturen, Spannung |
| `v_SolarDaily` | Tageswerte: Energie, Autarkie, Eigenverbrauch, Spitzenleistung, Ladestand min/max |
| `v_SolarMonthly` | Monatssummen |
| `Alerts` | Verlauf aller Meldungen |
| `Samples` | Rohwerte aller Register (begrenzte Aufbewahrung) |

Für SKx den Snapshot unter `http://<ip-des-pi>:8080/api/export/snapshot.db` laden und im Dashboard-Portal als SQLite-Verbindung (Provider `SQLite`, Dateipfad) einbinden. Die Views lassen sich dort als Dataset verwenden. Die Datenbank des laufenden Dienstes nicht über eine Netzwerkfreigabe öffnen: WAL funktioniert über Netzlaufwerke nicht zuverlässig.

Das Portal selbst läuft nicht auf dem Pi. DevExpress-Dashboards sind für 1 GB RAM zu schwer, deshalb sammelt der Pi nur und SKx analysiert auf dem PC oder Server.

CSV-Exporte (`/api/export/days.csv`, `/api/export/minutes.csv`, Parameter `from`/`to` als `yyyy-MM-dd`) sind für Excel in deutscher Einstellung formatiert.

## Vorzeichen und Berechnung

- Batterie negativ = laden, positiv = entladen. Netz positiv = Bezug, negativ = Einspeisung.
- Tageswerte entstehen aus den Gesamtzählern des Wechselrichters. Energie aus einem Ausfall des Pi zählt zum Tag, an dem er zurückkommt.
- Autarkie = 1 − Bezug / Verbrauch. Eigenverbrauch = 1 − Einspeisung / PV.
- Das Balkonkraftwerk (APsystems EZ1, lokaler Modus an Port 8050) läuft am Deye vorbei. Seine Leistung zählt darum zu PV und Verbrauch, Netz und Batterie bleiben wie gemessen. Prognosevergleich und Batterieprognose rechnen ohne es, weil sie sich auf die Dachflächen am Deye beziehen. Tage vor der Einrichtung bleiben unverändert.
- Der Batterie-Wirkungsgrad ist Entladung / Ladung über 30 Tage und wird erst ab 20 kWh Ladung angezeigt.

## Grenzen

- Die Registerbelegung gilt für dreiphasige Deye-Hybride und ist am Gerätetyp 6 (Hochvolt) geprüft. Andere Typen erzeugen eine Warnung im Log.
- Texte sind nur auf Deutsch.

## Entwicklung

```bash
dotnet test SKx.Solar.slnx
dotnet run --project SKx.Solar -- --Urls=http://localhost:5188
```
