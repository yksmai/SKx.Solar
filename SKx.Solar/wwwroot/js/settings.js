import { $, esc, getJson, num } from "./util.js";
import { DIRECTIONS, TILTS, compassToAzimuth, directionOption, modulesToKwp, tiltOption } from "./planes.js";

const value = v => v == null ? "" : esc(v);
const ct = eur => Math.round(eur * 10000) / 100;

const field = (label, name, v, attrs = "", unit = "") => `
  <label class="field"><span>${esc(label)}</span>
    <span class="field-input"><input type="number" step="any" inputmode="decimal" autocomplete="off" name="${name}" value="${value(v)}" ${attrs}>${unit ? `<em>${esc(unit)}</em>` : ""}</span>
  </label>`;

const text = (label, name, v, attrs = "", type = "text") => `
  <label class="field"><span>${esc(label)}</span>
    <span class="field-input"><input type="${type}" ${attrs} autocomplete="off" name="${name}" value="${value(v)}"></span>
  </label>`;

/** Der Server verrät nur, ob ein Geheimnis existiert. Leer lassen behält es, der Knopf entfernt es. */
const secret = (label, name, isSet) => `
  <label class="field"><span>${esc(label)}</span>
    <span class="field-input"><input type="password" autocomplete="new-password" name="${name}" placeholder="${isSet ? "gespeichert, leer lassen zum Behalten" : ""}">
    ${isSet ? `<button class="field-clear" type="button" data-clear="${name}">entfernen</button>` : ""}</span>
  </label>`;

const removeButton = label => `<button class="btn row-remove" type="button" data-remove aria-label="${esc(label)}">Entfernen</button>`;

const tariffRow = t => `
  <div class="settings-row" data-row="tariff">
    <label class="field"><span>Gültig ab</span><span class="field-input"><input type="date" name="from" value="${value(t?.from)}"></span></label>
    ${field("Bezug", "import", t ? ct(t.importEurPerKwh) : null, 'min="0"', "ct/kWh")}
    ${field("Einspeisung", "feedin", t ? ct(t.feedInEurPerKwh) : null, 'min="0"', "ct/kWh")}
    ${removeButton("Tarif entfernen")}
  </div>`;

const choice = (label, name, options, selected) => `
  <label class="field"><span>${esc(label)}</span>
    <span class="field-input"><select name="${name}" data-choice>${options.map(([v, l]) => `<option value="${v}" ${v === selected ? "selected" : ""}>${esc(l)}</option>`).join("")}</select></span>
  </label>`;

/** Jede Angabe gibt es in mehreren Formen, sichtbar ist nur die Eingabe zur gewählten. */
const planeRow = p => {
  const direction = directionOption(p?.azimuthDegrees ?? 0);
  const tilt = tiltOption(p?.tiltDegrees ?? 30);
  return `
  <div class="settings-row" data-row="plane">
    <div class="plane-part" data-part="kwpMode">
      ${choice("Leistung", "kwpMode", [["kwp", "Gesamt in kWp"], ["modules", "Module × Watt"]], "kwp")}
      <div data-when="kwp">${field("Gesamtleistung", "kwp", p?.kwp, 'min="0"', "kWp")}</div>
      <div data-when="modules" hidden><div class="plane-pair">
        ${field("Module", "modules", null, 'min="1"')}
        ${field("Je Modul", "moduleW", null, 'min="1"', "Wp")}
      </div><p class="stat-note" data-kwp-result></p></div>
    </div>
    <div class="plane-part" data-part="azimuthMode">
      ${choice("Ausrichtung", "azimuthMode", [...DIRECTIONS, ["compass", "Kompasswert"], ["degrees", "Winkel zu Süd"]], direction)}
      <div data-when="compass" hidden>${field("Kompass (Nord = 0°, Süd = 180°)", "compass", null, 'min="0" max="360"', "°")}</div>
      <div data-when="degrees" ${direction === "degrees" ? "" : "hidden"}>${field("Süd = 0°, Ost = -90°, West = 90°", "azimuth", p?.azimuthDegrees ?? 0, 'min="-180" max="180"', "°")}</div>
    </div>
    <div class="plane-part" data-part="tiltMode">
      ${choice("Neigung", "tiltMode", [...TILTS, ["degrees", "Genauer Wert"]], tilt)}
      <div data-when="degrees" ${tilt === "degrees" ? "" : "hidden"}>${field("Neigung", "tilt", p?.tiltDegrees ?? 30, 'min="0" max="90"', "°")}</div>
    </div>
    ${removeButton("Dachfläche entfernen")}
  </div>`;
};

/** Liest eine Dachfläche in der jeweils gewählten Form. Meldet fehlende Angaben hier, der Server sieht nur Zahlen. */
function readPlane(row) {
  const mode = name => row.querySelector(`[name="${name}"]`).value;
  const required = name => {
    const value = rowNumber(row, name);
    if (value == null) throw new Error("Bitte jede Dachfläche vollständig ausfüllen.");
    return value;
  };
  const azimuthMode = mode("azimuthMode");
  const tiltMode = mode("tiltMode");
  return {
    kwp: mode("kwpMode") === "modules" ? modulesToKwp(required("modules"), required("moduleW")) : required("kwp"),
    tiltDegrees: tiltMode === "degrees" ? required("tilt") : Number(tiltMode),
    azimuthDegrees: azimuthMode === "compass" ? compassToAzimuth(required("compass")) : azimuthMode === "degrees" ? required("azimuth") : Number(azimuthMode),
  };
}

function showModules(row) {
  const count = rowNumber(row, "modules"), watt = rowNumber(row, "moduleW");
  row.querySelector("[data-kwp-result]").textContent = count && watt ? `Ergibt ${num(modulesToKwp(count, watt), 2)} kWp` : "";
}

const formHtml = s => `
  <section class="card"><h2>Einstellungen</h2>
    <form id="settings-form" class="settings" novalidate>
      ${s.inverter ? `<fieldset><legend>Wechselrichter</legend>
        <p class="stat-note">Deye mit Solarman-WLAN-Logger. Die Adresse steht in der Geräteliste des Routers. Ohne Seriennummer sucht die App den Logger selbst, das klappt nur im selben Netz.</p>
        <div class="settings-grid">
          ${text("Adresse des Loggers", "inverterHost", s.inverter.host, 'placeholder="z. B. 192.168.1.50"')}
          ${field("Port", "inverterPort", s.inverter.port, 'min="1" max="65535"')}
          ${field("Seriennummer des Loggers", "loggerSerial", s.inverter.loggerSerial, 'min="1" step="1" inputmode="numeric" placeholder="automatisch"')}
        </div>
      </fieldset>` : ""}
      <fieldset><legend>Anlage</legend>
        <div class="settings-grid">
          ${field("Nutzbare Batteriekapazität", "capacity", s.batteryCapacityKwh, 'min="0"', "kWh")}
          ${field("Investition", "investment", s.investmentEur, 'min="0"', "€")}
        </div>
      </fieldset>
      <fieldset><legend>Balkonkraftwerk</legend>
        <p class="stat-note">APsystems EZ1 im lokalen Modus. Es zählt zu Erzeugung und Verbrauch. Leer lassen, wenn es keines gibt.</p>
        <div class="settings-grid">
          ${text("Adresse", "balconyHost", s.balcony.host)}
          ${field("Port", "balconyPort", s.balcony.port, 'min="1" max="65535"')}
        </div>
      </fieldset>
      <fieldset><legend>Tarife</legend>
        <p class="stat-note">Ein neuer Eintrag hält eine Preisänderung fest, ältere Tage rechnen weiter mit dem alten Preis.</p>
        <div id="tariff-rows">${s.tariffs.map(tariffRow).join("")}</div>
        <button class="btn" type="button" data-add="tariff">Tarif hinzufügen</button>
      </fieldset>
      <fieldset><legend>Ertragsprognose</legend>
        <p class="stat-note">Abgefragt wird Open-Meteo, dafür verlassen nur Koordinaten und Dachdaten das Heimnetz.</p>
        <div class="settings-grid">
          ${field("Breitengrad", "latitude", s.forecast.latitude, 'min="-90" max="90"', "°")}
          ${field("Längengrad", "longitude", s.forecast.longitude, 'min="-180" max="180"', "°")}
          ${field("Verlustfaktor", "ratio", s.forecast.performanceRatio, 'min="0" max="1"')}
        </div>
        <p class="stat-note">Trag ein, was du vorliegen hast. Eine ungefähre Angabe reicht für den Anfang.</p>
        <details class="plane-help"><summary>Woher bekomme ich die Werte?</summary>
          <ul>
            <li><b>Leistung:</b> Rechnung, Angebot oder Inbetriebnahmeprotokoll. Sonst Module zählen und die Wattzahl vom Typenschild nehmen.</li>
            <li><b>Ausrichtung:</b> Im Satellitenbild steht die Dachfläche im rechten Winkel zum First. Oder den Handy-Kompass parallel zur Dachfläche halten, in die Richtung, in die sie abfällt.</li>
            <li><b>Neigung:</b> Bauplan oder Installationsunterlagen. Sonst eine Neigungsmesser-App auf eine Dachschräge im Dachgeschoss legen.</li>
          </ul>
        </details>
        <div id="plane-rows">${s.forecast.planes.map(planeRow).join("")}</div>
        <button class="btn" type="button" data-add="plane">Dachfläche hinzufügen</button>
      </fieldset>
      <fieldset><legend>Börsenstrompreis</legend>
        <p class="stat-note">Daten von aWATTar. Die App zeigt die Preise nur an und steuert nichts.</p>
        <label class="switch"><input type="checkbox" name="pricesEnabled" ${s.dynamicPrices.enabled ? "checked" : ""}><span>Börsenpreise anzeigen</span></label>
        <div class="settings-grid">
          <label class="field"><span>Land</span><span class="field-input"><select name="country">
            <option value="de" ${s.dynamicPrices.country === "at" ? "" : "selected"}>Deutschland</option>
            <option value="at" ${s.dynamicPrices.country === "at" ? "selected" : ""}>Österreich</option>
          </select></span></label>
          ${field("Aufschlag", "surcharge", s.dynamicPrices.surchargeCtPerKwh, 'min="0"', "ct/kWh")}
          ${field("Umsatzsteuer", "vat", s.dynamicPrices.vatPercent, 'min="0" max="100"', "%")}
        </div>
      </fieldset>
      <fieldset><legend>Benachrichtigungen</legend>
        <p class="stat-note">Für ntfy reicht ein schwer erratbares Thema, das du in der ntfy-App abonnierst. Telegram braucht Bot-Token und Chat-ID.</p>
        <div class="settings-grid">
          ${text("ntfy-Server", "ntfyServer", s.alerts.ntfyServer, "", "url")}
          ${text("ntfy-Thema", "ntfyTopic", s.alerts.ntfyTopic)}
          ${secret("Telegram-Bot-Token", "telegramToken", s.telegramTokenSet)}
          ${text("Telegram-Chat-ID", "telegramChat", s.alerts.telegramChatId)}
          <label class="field"><span>Melden ab</span><span class="field-input"><select name="severity">
            ${[["info", "Hinweis"], ["warning", "Warnung"], ["critical", "Kritisch"]].map(([k, l]) => `<option value="${k}" ${s.alerts.minNotifySeverity === k ? "selected" : ""}>${l}</option>`).join("")}
          </select></span></label>
          ${field("Batterie fast leer unter", "batteryLow", s.alerts.batteryLowPercent, 'min="0" max="100"', "%")}
          ${field("Wechselrichter warm ab", "tempLimit", s.alerts.inverterTempLimitC, 'min="30" max="120"', "°C")}
        </div>
        <button class="btn" type="button" id="settings-test">Testnachricht senden</button>
      </fieldset>
      <fieldset><legend>MQTT und Home Assistant</legend>
        <p class="stat-note">Die App sendet nur Messwerte und empfängt keine Befehle.</p>
        <div class="settings-grid">
          ${text("Broker", "mqttHost", s.mqtt.host)}
          ${field("Port", "mqttPort", s.mqtt.port, 'min="1" max="65535"')}
          ${text("Benutzer", "mqttUser", s.mqtt.user)}
          ${secret("Passwort", "mqttPassword", s.mqttPasswordSet)}
          ${text("Thema", "mqttTopic", s.mqtt.topic)}
        </div>
        <label class="switch"><input type="checkbox" name="homeAssistant" ${s.mqtt.homeAssistant ? "checked" : ""}><span>Sensoren automatisch in Home Assistant anlegen</span></label>
      </fieldset>
      <fieldset><legend>Spiegel-Server</legend>
        <p class="stat-note">Schickt die Messwerte an einen eigenen Server, damit du sie auch unterwegs siehst. Fällt er aus, misst der Pi weiter und holt die Lücke später nach.</p>
        <div class="settings-grid">
          ${text("Adresse", "syncUrl", s.sync.url, 'placeholder="https://solar.example.de"')}
          ${secret("Token", "syncToken", s.syncTokenSet)}
          ${field("Intervall", "syncInterval", s.sync.intervalSeconds, 'min="5" max="3600"', "s")}
        </div>
      </fieldset>
      ${s.backup ? `<fieldset><legend>Datensicherung</legend>
        <p class="stat-note">Sichert die Datenbank und die Einstellungen einmal am Tag. Erst ein Ordner auf einem NAS schützt auch vor einem Ausfall der SD-Karte.</p>
        <label class="switch"><input type="checkbox" name="backupEnabled" ${s.backup.enabled ? "checked" : ""}><span>Täglich sichern</span></label>
        <div class="settings-grid">
          ${text("Ordner", "backupDirectory", s.backup.directory, 'placeholder="neben der Datenbank"')}
          ${field("Ab", "backupHour", s.backup.hour, 'min="0" max="23" step="1"', "Uhr")}
          ${field("Aufbewahren", "backupKeepDays", s.backup.keepDays, 'min="1" max="3650" step="1"', "Tage")}
        </div>
      </fieldset>` : ""}
      <fieldset><legend>Speicher</legend>
        <label class="switch"><input type="checkbox" name="sdCardMode" ${s.sdCardMode ? "checked" : ""}><span>SD-Karte schonen</span></label>
        <p class="stat-note">Schreibt alle 5 statt jede Minute und behält einen Rohwert pro Minute für 14 Tage. Bei einem Stromausfall fehlen im Tagesverlauf bis zu 5 Minuten, die Energiesummen stimmen trotzdem. Die Tageswerte laufen dafür bis zu 5 Minuten nach.</p>
      </fieldset>
      <div class="settings-actions">
        <span id="settings-status" class="settings-status" role="status"></span>
        <button class="btn btn-primary" type="submit" id="settings-save" disabled>Speichern</button>
      </div>
    </form>
  </section>`;

function number(form, name) {
  const input = form.elements[name];
  return input.value === "" ? null : Number(input.value);
}

const optional = (form, name) => form.elements[name].value.trim() || null;

/** Null lässt das gespeicherte Geheimnis stehen, leer entfernt es. Eine neue Eingabe gewinnt immer. */
function secretValue(form, name) {
  const input = form.elements[name];
  if (input.value) return input.value;
  return input.dataset.clear ? "" : null;
}

const rowNumber = (row, name) => {
  const input = row.querySelector(`[name="${name}"]`);
  return input.value === "" ? null : Number(input.value);
};

/** Liest das Formular. Leere Zeilenfelder meldet es hier, weil der Server dort nur Zahlen annimmt. */
function read(form) {
  const rows = kind => [...form.querySelectorAll(`[data-row="${kind}"]`)];
  const incomplete = (kind, names) => rows(kind).some(row => names.some(n => row.querySelector(`[name="${n}"]`).value === ""));
  if (incomplete("tariff", ["from", "import", "feedin"])) throw new Error("Bitte jeden Tarif vollständig ausfüllen.");
  return {
    inverter: form.elements.inverterHost ? {
      host: optional(form, "inverterHost"),
      port: number(form, "inverterPort") ?? 8899,
      loggerSerial: number(form, "loggerSerial"),
    } : null,
    backup: form.elements.backupEnabled ? {
      enabled: form.elements.backupEnabled.checked,
      directory: optional(form, "backupDirectory"),
      hour: number(form, "backupHour") ?? 3,
      keepDays: number(form, "backupKeepDays") ?? 14,
    } : null,
    batteryCapacityKwh: number(form, "capacity"),
    investmentEur: number(form, "investment"),
    tariffs: rows("tariff").map(row => ({
      from: row.querySelector('[name="from"]').value,
      importEurPerKwh: rowNumber(row, "import") / 100,
      feedInEurPerKwh: rowNumber(row, "feedin") / 100,
    })),
    forecast: {
      latitude: number(form, "latitude"),
      longitude: number(form, "longitude"),
      performanceRatio: number(form, "ratio") ?? 0.8,
      planes: rows("plane").map(readPlane),
    },
    dynamicPrices: {
      enabled: form.elements.pricesEnabled.checked,
      country: form.elements.country.value,
      surchargeCtPerKwh: number(form, "surcharge") ?? 0,
      vatPercent: number(form, "vat") ?? 0,
    },
    alerts: {
      ntfyServer: optional(form, "ntfyServer"),
      ntfyTopic: optional(form, "ntfyTopic"),
      telegramBotToken: secretValue(form, "telegramToken"),
      telegramChatId: optional(form, "telegramChat"),
      minNotifySeverity: form.elements.severity.value,
      batteryLowPercent: number(form, "batteryLow") ?? 15,
      inverterTempLimitC: number(form, "tempLimit") ?? 75,
    },
    mqtt: {
      host: optional(form, "mqttHost"),
      port: number(form, "mqttPort") ?? 1883,
      user: optional(form, "mqttUser"),
      password: secretValue(form, "mqttPassword"),
      topic: form.elements.mqttTopic.value.trim(),
      homeAssistant: form.elements.homeAssistant.checked,
    },
    balcony: { host: optional(form, "balconyHost"), port: number(form, "balconyPort") ?? 8050 },
    sync: { url: optional(form, "syncUrl"), token: secretValue(form, "syncToken"), intervalSeconds: number(form, "syncInterval") ?? 30 },
    sdCardMode: form.elements.sdCardMode.checked,
  };
}

async function save(settings) {
  const response = await fetch("api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
  });
  if (response.status === 401) location.href = "login.html";
  if (!response.ok) {
    const body = await response.json().catch(() => null);
    throw new Error(body?.error ?? "Speichern fehlgeschlagen.");
  }
}

/** Ungespeicherte Eingaben überstehen einen Seitenwechsel, sonst zeigt das Formular den Stand des Servers. */
export async function renderSettings(onSaved) {
  const host = $("planning-settings");
  if (host.querySelector("#settings-form")?.dataset.dirty) return;
  host.innerHTML = formHtml(await getJson("api/settings"));

  const form = $("settings-form");
  const saveButton = $("settings-save");
  const status = $("settings-status");
  const show = (text, kind = "") => {
    status.textContent = text;
    status.dataset.kind = kind;
  };
  const touched = () => {
    form.dataset.dirty = "1";
    saveButton.disabled = false;
    show("");
  };

  form.addEventListener("input", e => {
    touched();
    if (e.target.matches('[name="modules"], [name="moduleW"]')) showModules(e.target.closest("[data-row]"));
    if (e.target.matches("[data-choice]")) {
      e.target.closest("[data-part]").querySelectorAll("[data-when]").forEach(el => { el.hidden = el.dataset.when !== e.target.value; });
    }
  });
  form.addEventListener("click", e => {
    const clear = e.target.closest("[data-clear]");
    const add = e.target.closest("[data-add]");
    if (clear) {
      const input = form.elements[clear.dataset.clear];
      input.value = "";
      input.dataset.clear = "1";
      input.placeholder = "wird beim Speichern entfernt";
      clear.remove();
      touched();
    } else if (add) {
      const kind = add.dataset.add;
      const list = $(`${kind}-rows`);
      list.insertAdjacentHTML("beforeend", kind === "tariff" ? tariffRow(null) : planeRow(null));
      list.lastElementChild.querySelector("input").focus();
      touched();
    } else if (e.target.closest("[data-remove]")) {
      e.target.closest("[data-row]").remove();
      touched();
    }
  });
  $("settings-test").onclick = async () => {
    if (form.dataset.dirty) {
      show("Bitte erst speichern, getestet wird der gespeicherte Stand.", "error");
      return;
    }
    show("Wird gesendet …");
    const response = await fetch("api/notifications/test", { method: "POST" }).catch(() => null);
    if (response?.ok) show("Testnachricht verschickt", "ok");
    else show((await response?.json().catch(() => null))?.error ?? "Der Versand ist fehlgeschlagen.", "error");
  };
  form.addEventListener("submit", async e => {
    e.preventDefault();
    saveButton.disabled = true;
    try {
      await save(read(form));
      delete form.dataset.dirty;
      form.querySelectorAll('input[type="password"]').forEach(input => {
        if (input.value) input.placeholder = "gespeichert, leer lassen zum Behalten";
        else if (input.dataset.clear) input.placeholder = "";
        input.value = "";
        delete input.dataset.clear;
      });
      show("Gespeichert", "ok");
      await onSaved();
    } catch (error) {
      saveButton.disabled = false;
      show(error.message, "error");
    }
  });
}

/** Übernimmt den Vorschlag ins Formular. Gespeichert wird erst bewusst über den Knopf. */
export function suggestRatio(ratio) {
  const form = $("settings-form");
  if (!form) return;
  form.elements.ratio.value = ratio;
  form.elements.ratio.dispatchEvent(new Event("input", { bubbles: true }));
  form.elements.ratio.scrollIntoView({ block: "center" });
  form.elements.ratio.focus();
}

const ROLES = [["viewer", "Nur lesen"], ["admin", "Admin"]];
const roleOptions = selected => ROLES.map(([v, l]) => `<option value="${v}" ${v === selected ? "selected" : ""}>${l}</option>`).join("");

const userRow = (user, self) => `
  <div class="user-row" data-user="${esc(user.name)}">
    <strong>${esc(user.name)}</strong>
    ${self
      ? `<span class="muted">${user.role === "admin" ? "Admin" : "Nur lesen"}, dein Konto</span>`
      : `<select aria-label="Rolle von ${esc(user.name)}" data-role>${roleOptions(user.role)}</select>
         <button class="btn" type="button" data-reset>Neues Passwort</button>
         <button class="btn" type="button" data-delete>Löschen</button>`}
  </div>`;

/** Der Seitenabruf alle paar Sekunden darf Eingaben im Formular nicht überschreiben, darum nur die Liste neu zeichnen. */
export async function renderUsers(selfName) {
  const host = $("system-users");
  if (!host.firstChild) {
    host.innerHTML = `
      <section class="card"><h2>Benutzer</h2>
        <div id="user-list"></div>
        <form id="user-form" class="settings" style="margin-top:12px">
          <div class="settings-grid">
            ${text("Benutzername", "username", null, 'required maxlength="32" pattern="[\\p{L}\\p{N}._\\-]+" autocapitalize="none"')}
            ${text("Passwort", "password", null, 'required minlength="8"', "password")}
            ${choice("Rolle", "role", ROLES, "viewer")}
          </div>
          <div class="settings-actions">
            <span id="user-status" class="settings-status" role="status"></span>
            <button class="btn btn-primary" type="submit">Anlegen</button>
          </div>
        </form>
      </section>`;
    const show = (message, kind) => {
      const status = $("user-status");
      status.textContent = message;
      status.dataset.kind = kind;
    };
    const send = async (url, method, body, done) => {
      const response = await fetch(url, {
        method,
        headers: { "Content-Type": "application/json" },
        body: body ? JSON.stringify(body) : undefined,
      }).catch(() => null);
      if (response?.ok) show(done, "ok");
      else show((await response?.json().catch(() => null))?.error ?? "Das hat nicht geklappt.", "error");
      await drawUsers(selfName, true);
      return response?.ok;
    };
    const form = $("user-form");
    form.addEventListener("submit", async e => {
      e.preventDefault();
      const ok = await send("api/users", "POST", { username: form.elements.username.value, password: form.elements.password.value, role: form.elements.role.value }, "Angelegt");
      if (ok) form.reset();
    });
    const list = $("user-list");
    const target = el => encodeURIComponent(el.closest("[data-user]").dataset.user);
    list.addEventListener("change", e => {
      if (e.target.matches("[data-role]")) send(`api/users/${target(e.target)}`, "PUT", { role: e.target.value }, "Rolle geändert");
    });
    list.addEventListener("click", e => {
      if (e.target.matches("[data-delete]")) {
        const name = e.target.closest("[data-user]").dataset.user;
        if (confirm(`${name} wirklich löschen?`)) send(`api/users/${target(e.target)}`, "DELETE", null, "Gelöscht");
      } else if (e.target.matches("[data-reset]")) {
        const password = prompt("Neues Passwort (mindestens 8 Zeichen):");
        if (password) send(`api/users/${target(e.target)}`, "PUT", { password }, "Passwort geändert");
      }
    });
  }
  await drawUsers(selfName);
}

async function drawUsers(selfName, force = false) {
  const list = $("user-list");
  // Eine offene Auswahl würde beim Neuzeichnen zuklappen.
  if (!force && list.contains(document.activeElement)) return;
  const users = await getJson("api/users");
  list.innerHTML = users.map(u => userRow(u, u.name === selfName)).join("");
}

export function renderPasswordForm() {
  const host = $("system-password");
  if (host.firstChild) return;
  host.innerHTML = `
    <section class="card"><h2>Passwort ändern</h2>
      <form id="password-form" class="settings">
        <div class="settings-grid">
          ${text("Aktuelles Passwort", "current", null, 'required autocomplete="current-password"', "password")}
          ${text("Neues Passwort", "next", null, 'required minlength="8" autocomplete="new-password"', "password")}
        </div>
        <div class="settings-actions">
          <span id="password-status" class="settings-status" role="status"></span>
          <button class="btn btn-primary" type="submit">Ändern</button>
        </div>
      </form>
    </section>`;
  const form = $("password-form");
  const status = $("password-status");
  form.addEventListener("submit", async e => {
    e.preventDefault();
    const response = await fetch("api/password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ current: form.elements.current.value, next: form.elements.next.value }),
    }).catch(() => null);
    if (response?.ok) {
      form.reset();
      status.textContent = "Geändert";
      status.dataset.kind = "ok";
    } else {
      status.textContent = (await response?.json().catch(() => null))?.error ?? "Das hat nicht geklappt.";
      status.dataset.kind = "error";
    }
  });
}
