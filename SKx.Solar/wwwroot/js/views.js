import { $, esc, getJson, kwh, morph, num, pct, reducedMotion, svgNode as el, tooltip, watt } from "./util.js";
import { drawBars, drawTimeline } from "./charts.js";
import { renderPasswordForm, renderSettings, renderUsers, suggestRatio } from "./settings.js";

const setup = (title, text, config) => `
  <div class="setup"><strong>${esc(title)}</strong><span>${esc(text)}</span>${config ? `<pre>${esc(config)}</pre>` : ""}</div>`;
const fact = (label, value) => `<div class="fact"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
const dateTime = ts => new Date(ts * 1000).toLocaleString("de-DE", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
const dateText = text => new Date(`${text}T12:00`).toLocaleDateString("de-DE", { weekday: "short", day: "2-digit", month: "2-digit", year: "numeric" });
const euro = (v, digits = 2) => v == null ? "–" : `${num(v, digits)} €`;
const SEVERITY = { critical: "Kritisch", warning: "Warnung", info: "Hinweis" };

/* ---------- Meldungen ---------- */
export async function renderAlerts() {
  const alerts = await getJson("api/alerts");
  const host = $("alerts-content");
  const openCount = alerts.filter(a => a.endedTs == null).length;
  const items = alerts.map((a, i) => `
    <article class="alert-item${a.endedTs == null ? "" : " closed"}" style="--i:${Math.min(i, 12)}">
      <span class="sev" data-severity="${esc(a.severity)}">${esc(SEVERITY[a.severity] ?? a.severity)}</span>
      <div><strong>${esc(a.title)}</strong><p>${esc(a.message)}</p></div>
      <time>${dateTime(a.startedTs)}<br>${a.endedTs == null ? "läuft noch" : `bis ${dateTime(a.endedTs)}`}</time>
    </article>`).join("");
  morph(host, `
    <section class="card">
      <div class="card-head"><div><h2>Meldungen</h2><p class="muted range-summary">${openCount ? `${openCount} aktiv` : "Nichts aktiv"} · Fehlercodes stammen direkt vom Gerät</p></div></div>
      ${alerts.length ? `<div class="alert-list">${items}</div>` : `
        <div class="empty-state"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/></svg><span>Bisher keine Meldungen.</span></div>`}
    </section>`);
}

/* ---------- Analyse ---------- */
export async function renderAnalysis(openDay) {
  const d = await getJson("api/insights");
  const b = d.battery;
  const maxBin = Math.max(1, ...b.socHistogram);
  const totalBins = b.socHistogram.reduce((a, v) => a + v, 0);
  const strings = d.strings;
  const peak = Math.max(strings.pv1W ?? 0, strings.pv2W ?? 0, 1);

  $("analysis-content").innerHTML = `
    <div class="cols-2">
      <section class="card">
        <h2>Batterie</h2>
        <div class="facts">
          ${fact("Vollzyklen", b.cycles == null ? "Kapazität fehlt" : num(b.cycles, 1))}
          ${fact("Wirkungsgrad (30 Tage)", b.efficiencyPct == null ? "noch zu wenig Daten" : `${num(b.efficiencyPct, 1)} %`)}
          ${fact("Geladen (30 Tage)", kwh(b.chargedKwh))}
          ${fact("Entladen (30 Tage)", kwh(b.dischargedKwh))}
          ${fact("Temperatur", b.tempMinC == null ? "–" : `${num(b.tempMinC)} bis ${num(b.tempMaxC)} °C`)}
        </div>
        ${b.capacityKwh == null ? `<div style="margin-top:12px">${setup("Zyklen freischalten", "Die nutzbare Batteriekapazität unter Planung in den Einstellungen eintragen.")}</div>` : ""}
      </section>
      <section class="card">
        <h2>Ladestand-Verteilung (30 Tage)</h2>
        <div class="hist" id="soc-hist">
          ${b.socHistogram.map((v, i) => `<div data-label="${i * 10}–${i === 9 ? 100 : i * 10 + 9} %" data-value="${totalBins ? pct(v / totalBins * 100) : "–"}"><i style="--v:${(v / maxBin).toFixed(3)};--i:${i}"></i></div>`).join("")}
        </div>
        <div class="hist-axis"><span>0 %</span><span>50 %</span><span>100 %</span></div>
      </section>
    </div>
    <section class="card">
      <h2>${esc(d.year)} im Vergleich zu ${esc(d.previousYear)} (kWh Erzeugung)</h2>
      <div class="legend" id="compare-legend">
        <span style="--c:var(--series-pv)"><i></i>${esc(d.year)}</span>
        <span style="--c:var(--muted)"><i></i>${esc(d.previousYear)}</span>
      </div>
      <div class="chart" id="compare-chart" style="height:240px"></div>
    </section>
    <div class="cols-2">
      <section class="card">
        <h2>Beste Tage</h2>
        <div class="rank" id="best-days">
          ${d.bestDays.map((x, i) => `<button type="button" data-date="${esc(x.date)}"><span>${i + 1}</span><span>${esc(dateText(x.date))}</span><div class="meter"><i style="--v:${(x.pvKwh / Math.max(d.bestDays[0].pvKwh, 1)).toFixed(3)};--c:var(--series-pv)"></i></div><strong>${kwh(x.pvKwh)}</strong></button>`).join("") || `<p class="muted">Noch keine Daten.</p>`}
        </div>
      </section>
      <section class="card">
        <h2>Strings im Vergleich (bis zu 30 Tage)</h2>
        ${strings.pv1W == null ? `<p class="muted">Sobald Rohwerte mit Erzeugung vorliegen, erscheint hier der Vergleich.</p>` : `
          <div class="meter-row"><span>String 1</span><div class="meter"><i style="--v:${(strings.pv1W / peak).toFixed(3)};--c:var(--series-pv)"></i></div><span>${watt(strings.pv1W)}</span></div>
          <div class="meter-row"><span>String 2</span><div class="meter"><i style="--v:${(strings.pv2W / peak).toFixed(3)};--c:var(--series-pv)"></i></div><span>${watt(strings.pv2W)}</span></div>
          <p class="muted stat-note">Mittlere Leistung, solange die Anlage produziert. Unterschiedliche Modulzahlen oder Ausrichtungen sind normal, ein plötzlich wachsender Abstand nicht.</p>`}
      </section>
    </div>`;

  $("best-days").addEventListener("click", e => {
    const button = e.target.closest("[data-date]");
    if (button) openDay(new Date(`${button.dataset.date}T12:00`));
  });
  $("soc-hist").addEventListener("pointermove", e => {
    const bar = e.target.closest("[data-label]");
    if (bar) tooltip.show(`<b>${esc(bar.dataset.label)}</b>${tooltip.row("Anteil der Zeit", bar.dataset.value)}`, e.clientX, e.clientY);
  });
  $("soc-hist").addEventListener("pointerleave", () => tooltip.hide());

  const names = Array.from({ length: 12 }, (_, i) => new Date(2000, i, 1).toLocaleDateString("de-DE", { month: "short" }).replace(".", ""));
  const lookup = (rows, i) => rows.find(x => Number(x.period.slice(5)) === i + 1)?.pvKwh ?? 0;
  const items = names.map((_, i) => {
    const current = lookup(d.months, i), previous = lookup(d.previousMonths, i);
    return current || previous ? { current, previous } : null;
  });
  drawBars($("compare-chart"), items, [
    { key: "current", label: d.year, color: "var(--series-pv)" },
    { key: "previous", label: d.previousYear, color: "var(--muted)" },
  ], { animate: true, label: "Monatserzeugung im Jahresvergleich", xLabel: i => names[i], title: i => names[i] });
}

/* ---------- Planung ---------- */
function drawPrices(container, hours, cheapest) {
  container.innerHTML = "";
  const box = container.getBoundingClientRect();
  const w = Math.max(box.width, 240), h = box.height;
  const svg = el("svg", { viewBox: `0 0 ${w} ${h}`, role: "img", "aria-label": "Strompreis je Stunde" });
  container.append(svg);
  const left = 44, right = 6, top = 8, bottom = 22;
  const max = Math.max(...hours.map(x => x.ct), 1), min = Math.min(0, ...hours.map(x => x.ct));
  const y = v => top + (max - v) / (max - min) * (h - top - bottom);
  [min, max / 2, max].forEach(v => {
    svg.append(el("line", { class: v === 0 ? "zero" : "gridline", x1: left, x2: w - right, y1: y(v), y2: y(v) }));
    svg.append(el("text", { class: "axis", x: left - 8, y: y(v) + 4, "text-anchor": "end" }, `${num(v)} ct`));
  });
  const band = (w - left - right) / hours.length;
  const now = Date.now() / 1000;
  const grow = !reducedMotion();
  hours.forEach((hour, i) => {
    const isCheap = cheapest.includes(hour.t);
    const isNow = hour.t <= now && now < hour.t + 3600;
    const x = left + i * band + 1;
    const top0 = Math.min(y(hour.ct), y(0));
    const g = el("g", { class: "bar-group" });
    g.append(el("rect", { x: left + i * band, y: top, width: band, height: h - top - bottom, fill: "transparent" }));
    g.append(el("rect", {
      class: `bar${grow ? " grow" : ""}`, style: `--i:${i}`, x, y: top0, width: Math.max(band - 2, 1), height: Math.max(Math.abs(y(hour.ct) - y(0)), 1), rx: 3,
      fill: isCheap ? "var(--series-battery)" : isNow ? "var(--text)" : "var(--surface-3)",
    }));
    const time = new Date(hour.t * 1000);
    if (time.getHours() % (w < 520 ? 6 : 3) === 0)
      svg.append(el("text", { class: "axis", x: x + band / 2, y: h - 6, "text-anchor": "middle" }, `${String(time.getHours()).padStart(2, "0")}:00`));
    const content = `<b>${time.toLocaleString("de-DE", { weekday: "short", hour: "2-digit", minute: "2-digit" })} Uhr</b>${tooltip.row("Preis", `${num(hour.ct, 1)} ct/kWh`)}${isCheap ? tooltip.row("Einordnung", "günstigste Stunden") : ""}`;
    g.addEventListener("pointermove", e => tooltip.show(content, e.clientX, e.clientY));
    g.addEventListener("pointerleave", () => tooltip.hide());
    svg.append(g);
  });
}

/** Blendet aus, was das Konto nicht darf. Prüfen tut der Server, das hier spart nur tote Knöpfe. */
async function currentUser() {
  const me = await getJson("api/me");
  document.body.classList.toggle("viewer", !me.admin);
  return me;
}

export const renderPlanning = async () => {
  const me = await currentUser();
  await Promise.all([drawPlanning(), me.admin ? renderSettings(afterSettingsSaved) : null]);
};

async function afterSettingsSaved() {
  await drawPlanning();
  // Prognose und Preise lädt der Server im Hintergrund neu.
  setTimeout(() => { if ($("planning-content").offsetParent) drawPlanning().catch(() => {}); }, 3000);
}

const clock = ts => new Date(ts * 1000).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
const dayName = ts => {
  const date = new Date(ts * 1000);
  const tomorrow = new Date(); tomorrow.setDate(tomorrow.getDate() + 1);
  if (date.toDateString() === new Date().toDateString()) return "Heute";
  return date.toDateString() === tomorrow.toDateString() ? "Morgen" : date.toLocaleDateString("de-DE", { weekday: "long" });
};

function adviceBody(a) {
  if (!a.configured) return setup("Empfehlungen einrichten", "Standort und Dachflächen unten in den Einstellungen eintragen. Für die Batterieprognose zusätzlich die Kapazität.");
  const w = a.window;
  const use = w
    ? `<div class="advice"><strong>${esc(dayName(w.startTs))} ${clock(w.startTs)} bis ${clock(w.endTs)} Uhr</strong>
        <span>Rund ${esc(watt(w.avgW))} Überschuss, gut für Waschmaschine, Spüler oder Wallbox. ${w.batteryFull ? "Die Batterie dürfte dann voll sein, der Strom bliebe sonst ungenutzt." : "Die Batterie lädt in dieser Zeit entsprechend langsamer."}</span></div>`
    : `<div class="advice"><strong>Kein größerer Überschuss in Sicht</strong><span>Heute und morgen liegt die erwartete Leistung kaum über dem üblichen Verbrauch.</span></div>`;
  const b = a.battery;
  const battery = !b ? "" : `<div class="advice"><strong>Batterie</strong><span>${[
    b.fullTs ? `Voll: ${esc(dayName(b.fullTs))} gegen ${clock(b.fullTs)} Uhr.` : "Wird in den nächsten 48 Stunden voraussichtlich nicht voll.",
    b.atReserve ? `Steht gerade an der Reserve von ${num(b.reservePercent)} %.` : b.emptyTs ? `Reicht bis ${esc(dayName(b.emptyTs)).toLowerCase()} etwa ${clock(b.emptyTs)} Uhr (Reserve ${num(b.reservePercent)} %).` : "Reicht voraussichtlich durchgehend.",
  ].join(" ")}</span></div>`;
  const price = !a.cheapest ? "" : `<div class="advice"><strong>Günstigster Börsenstrom</strong><span>${esc(dayName(a.cheapest.t))} ab ${clock(a.cheapest.t)} Uhr, ${num(a.cheapest.ct, 1)} ct/kWh.</span></div>`;
  return `<div class="advice-list">${use}${battery}${price}</div>
    <p class="stat-note">Schätzung aus Prognose und deinem Verbrauch der letzten 14 Tage. Die App schaltet nichts.</p>`;
}

function accuracyBody(a) {
  if (!a.days.length) return `<p class="muted">Der Vergleich beginnt, sobald ein ganzer Tag mit Prognose vorliegt.</p>`;
  const deviation = a.deviationPct == null ? "–" : `${a.deviationPct > 0 ? "+" : ""}${num(a.deviationPct)} %`;
  return `<div class="cols-3">
      <div><div class="stat">${deviation}</div><div class="stat-note">Ertrag gegenüber Prognose, ${num(a.days.length)} Tage</div></div>
      <div><div class="stat">${a.suggestedRatio == null ? "–" : num(a.suggestedRatio, 2)}</div><div class="stat-note">${a.suggestedRatio == null ? `Vorschlag ab 7 Tagen ohne volle Batterie (bisher ${a.usableDays})` : "vorgeschlagener Verlustfaktor"}</div></div>
      <div>${a.suggestedRatio == null ? "" : `<button class="btn" type="button" id="use-ratio">Ins Formular übernehmen</button>`}</div>
    </div>
    <div class="chart" id="accuracy-chart" style="height:160px"></div>
    <p class="stat-note">Ertrag in Prozent der Prognose. Fällt der Wert über Wochen, kann das an Verschmutzung oder neuer Verschattung liegen. An Tagen mit voller Batterie regelt die Anlage eventuell ab.</p>`;
}

function roofCheckBody(c) {
  if (c.verdict === "waiting")
    return `<p class="muted">Die Prüfung braucht ${num(c.neededDays)} klare Tage mit nicht ganz voller Batterie. Bisher ${num(c.clearDays)}.</p>`;
  const minutes = num(Math.abs(c.shiftMinutes));
  const text = c.verdict === "ok"
    ? `<strong>Passt zu deiner Eingabe</strong><span>Das Dach erzeugt im Mittel etwa ${minutes} Minuten neben dem erwarteten Zeitpunkt.</span>`
    : `<strong>Dein Dach zeigt vermutlich weiter nach ${c.verdict === "later" ? "Westen" : "Osten"}</strong><span>Die Erzeugung liegt im Mittel ${minutes} Minuten ${c.verdict === "later" ? "später" : "früher"} als nach deiner Eingabe erwartet.</span>`;
  return `<div class="advice-list"><div class="advice">${text}</div></div>
    <p class="stat-note">Grundlage: ${num(c.clearDays)} klare Tage der letzten 60. Verschattung am Morgen oder Abend kann das Ergebnis ebenfalls verschieben.</p>`;
}

async function drawPlanning() {
  const [forecast, prices, economy, advice, accuracy, roofCheck] = await Promise.all([getJson("api/forecast"), getJson("api/prices"), getJson("api/economy"), getJson("api/advice"), getJson("api/forecast-accuracy"), getJson("api/roof-check")]);
  const todayIso = new Date();
  const today = `${todayIso.getFullYear()}-${String(todayIso.getMonth() + 1).padStart(2, "0")}-${String(todayIso.getDate()).padStart(2, "0")}`;

  const forecastBody = !forecast.configured
    ? setup("Prognose einrichten", "Standort und Dachflächen unten in den Einstellungen eintragen.")
    : !forecast.days ? `<p class="muted">Die erste Prognose wird geladen.</p>` : `
      <div class="cols-3">${forecast.days.map(x => `<div><div class="stat">${kwh(x.kwh)}</div><div class="stat-note">${x.date === today ? "Heute" : esc(dateText(x.date))}</div></div>`).join("")}</div>
      <h3 style="margin:16px 0 4px">Erwartete Leistung heute</h3>
      <div class="chart" id="forecast-chart" style="height:180px"></div>
      <p class="stat-note">Schätzung aus Einstrahlung, Anlagengröße und Verlustfaktor. Verschattung kennt sie nicht.</p>`;

  const priceBody = !prices.configured
    ? setup("Preise einrichten", "Börsenstrompreise von aWATTar lassen sich unten in den Einstellungen einschalten.")
    : !prices.hours?.length ? `<p class="muted">Die Preise werden geladen.</p>` : `
      <div class="legend"><span style="--c:var(--series-battery)"><i></i>Günstigste 3 Stunden</span><span style="--c:var(--text)"><i></i>Jetzt</span></div>
      <div class="chart price-chart" id="price-chart"></div>
      <p class="stat-note">${prices.includesSurcharge ? "Inklusive deiner Aufschläge und Steuer." : "Reiner Börsenpreis ohne Netzentgelte, Abgaben und Steuer."} Die App zeigt die Preise nur an und steuert nichts.</p>`;

  const economyBody = !economy.configured
    ? setup("Wirtschaftlichkeit einrichten", "Tarif und Investition unten in den Einstellungen eintragen.")
    : `<div class="cols-3">
        <div><div class="stat">${euro(economy.totalSavingsEur)}</div><div class="stat-note">gespart in ${num(economy.measuredDays)} Messtagen</div></div>
        <div><div class="stat">${economy.investmentEur == null ? "–" : euro(economy.investmentEur, 0)}</div><div class="stat-note">Investition</div></div>
        <div><div class="stat">${economy.paybackYears == null ? "–" : `${num(economy.paybackYears, 1)} Jahre`}</div><div class="stat-note">${economy.investmentEur == null ? "Investition fehlt" : economy.paybackYears == null ? "ab 30 Messtagen" : "grobe Amortisation, hochgerechnet"}</div></div>
      </div>
      <h3 style="margin:16px 0 4px">Ersparnis pro Monat (€)</h3>
      <div class="chart" id="savings-chart" style="height:180px"></div>`;

  $("planning-content").innerHTML = `
    <section class="card"><h2>Empfehlung</h2>${adviceBody(advice)}</section>
    <section class="card"><h2>Ertragsprognose</h2>${forecastBody}</section>
    ${forecast.configured ? `<section class="card"><h2>Prognose gegen Ertrag</h2>${accuracyBody(accuracy)}</section>` : ""}
    ${roofCheck.configured ? `<section class="card"><h2>Passt die Dachausrichtung?</h2>${roofCheckBody(roofCheck)}</section>` : ""}
    <section class="card"><h2>Strompreis</h2>${priceBody}</section>
    <section class="card"><h2>Wirtschaftlichkeit</h2>${economyBody}</section>`;

  if ($("forecast-chart") && forecast.hours) {
    const start = new Date(); start.setHours(0, 0, 0, 0);
    const end = new Date(start); end.setDate(end.getDate() + 1);
    const points = forecast.hours.filter(x => x.t >= start / 1000 && x.t <= end / 1000).map(x => ({ t: x.t, forecast: x.w }));
    drawTimeline($("forecast-chart"), points, [{ key: "forecast", label: "Prognose", color: "var(--series-pv)", fill: true }], {
      start: start / 1000, end: end / 1000, gapSeconds: 3600, animate: true, label: "Erwartete Leistung heute", format: watt,
    });
  }
  if ($("use-ratio")) $("use-ratio").onclick = () => suggestRatio(accuracy.suggestedRatio);
  if ($("accuracy-chart")) {
    const days = accuracy.days;
    drawBars($("accuracy-chart"), days.map(d => ({ ratio: d.forecastKwh > 0 ? Math.round(d.pvKwh / d.forecastKwh * 100) : 0 })), [{ key: "ratio", label: "Ertrag in % der Prognose", color: "var(--series-pv)" }], {
      animate: true, label: "Ertrag in Prozent der Prognose",
      xLabel: i => days[i].date.slice(8),
      title: i => dateText(days[i].date),
      format: v => `${num(v)} %`,
    });
  }
  if ($("price-chart")) drawPrices($("price-chart"), prices.hours, prices.cheapest ?? []);
  if ($("savings-chart")) {
    const months = economy.months;
    drawBars($("savings-chart"), months.map(m => ({ savings: m.savingsEur ?? 0 })), [{ key: "savings", label: "Ersparnis", color: "var(--series-pv)" }], {
      animate: true, label: "Ersparnis pro Monat",
      xLabel: i => months[i].month.slice(5),
      title: i => new Date(`${months[i].month}-15`).toLocaleDateString("de-DE", { month: "long", year: "numeric" }),
      format: v => euro(v),
    });
  }
}

/* ---------- System ---------- */
export async function renderSystem() {
  const [s, me] = await Promise.all([getJson("api/system"), currentUser()]);
  const hours = Math.floor(s.uptimeSeconds / 3600);
  const integrations = s.integrations.map(i => {
    const state = !i.configured ? "off" : i.state?.error ? "error" : i.state?.lastOkTs ? "ok" : "wait";
    const label = { off: "aus", error: "Fehler", ok: "aktiv", wait: "wartet" }[state];
    const detail = i.state?.error ?? (i.state?.lastOkTs ? `zuletzt ${dateTime(i.state.lastOkTs)}` : "");
    return `<div class="integration"><span>${esc(i.name)}</span><span class="pill" data-state="${state}">${label}</span>${detail ? `<small>${esc(detail)}</small>` : ""}</div>`;
  }).join("");

  morph($("system-content"), `
    <div class="cols-2">
      <section class="card">
        <h2>Dienste</h2>
        ${integrations}
        <p class="stat-note" style="margin-top:10px">Einstellen kannst du das unter Planung, Adresse und Abfrage des Loggers stehen in <code>appsettings.json</code>.</p>
      </section>
      <section class="card">
        <h2>Zustand</h2>
        <div class="facts">
          ${fact("Version", s.version ?? "–")}
          ${fact("Laufzeit", hours >= 24 ? `${num(hours / 24, 1)} Tage` : `${hours} h`)}
          ${fact("Arbeitsspeicher", `${num(s.memoryMb)} MB`)}
          ${s.pi.cpuTempC == null ? "" : fact("CPU-Temperatur", `${num(s.pi.cpuTempC, 1)} °C`)}
          ${s.pi.load1 == null ? "" : fact("Last (1 Minute)", num(s.pi.load1, 2))}
          ${s.pi.memAvailableMb == null ? "" : fact("RAM frei", `${num(s.pi.memAvailableMb)} MB`)}
          ${s.pi.underVoltage == null ? "" : fact("Stromversorgung", s.pi.underVoltage ? "Unterspannung" : "in Ordnung")}
          ${s.pi.writtenMbPerDay == null ? "" : fact("Schreiblast der Karte", `${num(s.pi.writtenMbPerDay)} MB/Tag`)}
          ${fact("Kartenschonung", s.sdCardMode ? "an" : "aus")}
          ${fact("Datenbank", `${num(s.dbMb, 1)} MB`)}
          ${fact("Freier Speicher", `${num(s.diskFreeGb, 1)} GB`)}
          ${fact("Sicherungen", `${s.backups.count} von max. ${s.backups.keepDays + 1}`)}
          ${fact("Letzte Sicherung", s.backups.latest || "noch keine")}
          ${fact("Angemeldet als", `${me.name} (${me.admin ? "Admin" : "Nur lesen"})`)}
        </div>
        <p class="stat-note" style="margin-top:10px;overflow-wrap:anywhere">Sicherungsziel: <code>${esc(s.backups.directory)}</code></p>
        ${s.authEnabled ? `<button class="btn" id="logout" type="button" style="margin-top:12px">Abmelden</button>` : ""}
      </section>
    </div>`);
  if (s.authEnabled) renderPasswordForm();
  if (me.admin) await renderUsers(me.name);
  const logout = $("logout");
  if (logout) logout.onclick = async () => {
    await fetch("api/logout", { method: "POST" });
    location.href = "login.html";
  };
}
