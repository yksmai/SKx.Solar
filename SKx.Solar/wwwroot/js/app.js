import { $, esc, getJson, iso, kwh, morph, num, pct, reducedMotion, toast, tooltip, tween, watt } from "./util.js";
import { FlowView, computeFlows } from "./flow.js";
import { cursorGroup, drawBars, drawCalendar, drawSparkline, drawTimeline } from "./charts.js";
import { renderAlerts, renderAnalysis, renderPlanning, renderSystem as renderSystemPage } from "./views.js";

const COLORS = { pv: "var(--series-pv)", load: "var(--series-load)", battery: "var(--series-battery)", grid: "var(--series-grid)", balcony: "var(--series-balcony)" };
const SERIES = [
  { key: "pv", label: "PV", color: COLORS.pv, fill: true },
  { key: "load", label: "Verbrauch", color: COLORS.load },
  { key: "battery", label: "Batterie (+ entladen, − laden)", short: "Batterie", color: COLORS.battery },
  { key: "grid", label: "Netz (+ Bezug, − Einspeisung)", short: "Netz", color: COLORS.grid },
];
const TOTAL_COLOR = "var(--series-total)";
const LOAD_BAR = { key: "loadKwh", label: "Verbrauch", color: COLORS.load, toggle: "load" };
const GENERATION_BARS = [
  { key: "pvKwh", label: "Gesamt", color: TOTAL_COLOR, toggle: "pv" },
  { key: "roofKwh", label: "Dach", color: COLORS.pv, toggle: "roof" },
  { key: "balconyKwh", label: "Balkon", color: COLORS.balcony, toggle: "balcony" },
];
const HIDDEN_KEY = "skx-solar-hidden-series";

function loadHidden() {
  try { return new Set(JSON.parse(localStorage.getItem(HIDDEN_KEY)) ?? []); } catch { return new Set(); }
}

function saveHidden() {
  try { localStorage.setItem(HIDDEN_KEY, JSON.stringify([...state.hidden])); } catch { /* nur Komfort */ }
}

const toggleOf = s => s.toggle ?? s.key;
const isShown = s => !state.hidden.has(toggleOf(s));

/** Ohne Balkon gibt es nur eine Erzeugung, dann bleibt es bei der bisherigen PV-Linie. */
const daySeries = balcony => balcony ? [
  { key: "pv", label: "Gesamt", color: TOTAL_COLOR, fill: true },
  { key: "roof", label: "Dach", color: COLORS.pv },
  { key: "balconyW", label: "Balkon", color: COLORS.balcony, toggle: "balcony" },
  ...SERIES.slice(1),
] : SERIES;

const energyLegend = balcony => balcony ? [...GENERATION_BARS, LOAD_BAR] : [{ key: "pvKwh", label: "PV", color: COLORS.pv, toggle: "pv" }, LOAD_BAR];

/** Sind alle drei an, ist die Höhe des Stapels aus Dach und Balkon die Summe. Sonst stehen sie nebeneinander. */
function energyBars(balcony) {
  const legend = energyLegend(balcony);
  const shown = legend.filter(isShown);
  const stacked = balcony && GENERATION_BARS.every(isShown);
  const bars = stacked ? [
    { ...GENERATION_BARS[2], col: "generation" },
    { ...GENERATION_BARS[1], col: "generation" },
    ...shown.filter(s => s === LOAD_BAR),
  ] : shown.length ? shown : legend;
  return { bars, stacked };
}

const withRoof = item => item && { ...item, roofKwh: Math.round((item.pvKwh - item.balconyKwh) * 10) / 10 };

const state = {
  mode: "day",
  date: new Date(),
  hidden: loadHidden(),
  legendKeys: [],
  live: null,
  todayPoints: [],
  systemTab: "pv",
  calendarYear: null,
  draw: null,
  drawn: "",
};

/* ---------- Design und Mikrointeraktionen ---------- */
function initTheme() {
  $("theme").addEventListener("click", e => {
    const root = document.documentElement;
    const current = root.dataset.theme || (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
    const next = current === "dark" ? "light" : "dark";
    const apply = () => {
      root.dataset.theme = next;
      try { localStorage.setItem("skx-solar-theme", next); } catch { /* nur Komfort */ }
    };
    if (!document.startViewTransition || reducedMotion()) { apply(); return; }
    // Das neue Design breitet sich kreisförmig vom Schalter aus.
    const radius = Math.hypot(Math.max(e.clientX, innerWidth - e.clientX), Math.max(e.clientY, innerHeight - e.clientY));
    document.startViewTransition(apply).ready.then(() => {
      document.documentElement.animate(
        { clipPath: [`circle(0 at ${e.clientX}px ${e.clientY}px)`, `circle(${radius}px at ${e.clientX}px ${e.clientY}px)`] },
        { duration: 550, easing: "cubic-bezier(.2,.8,.2,1)", pseudoElement: "::view-transition-new(root)" });
    });
  });
}

function initMicro() {
  document.addEventListener("pointerdown", e => {
    const button = e.target.closest(".btn, .icon-btn");
    if (!button || button.disabled || reducedMotion()) return;
    const r = button.getBoundingClientRect();
    const ripple = document.createElement("span");
    ripple.className = "ripple";
    ripple.style.left = `${e.clientX - r.left}px`;
    ripple.style.top = `${e.clientY - r.top}px`;
    button.append(ripple);
    ripple.addEventListener("animationend", () => ripple.remove());
  });

  let frame = 0;
  document.addEventListener("pointermove", e => {
    if (frame || e.pointerType === "touch") return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      const card = e.target.closest?.(".card");
      if (!card) return;
      const r = card.getBoundingClientRect();
      const x = e.clientX - r.left, y = e.clientY - r.top;
      card.style.setProperty("--mx", `${x}px`);
      card.style.setProperty("--my", `${y}px`);
      // Die aktive Solarzelle rastet im 36-px-Raster des Moduls ein.
      card.style.setProperty("--cx", `${Math.floor(x / 36) * 36 + 1}px`);
      card.style.setProperty("--cy", `${Math.floor(y / 36) * 36 + 1}px`);
    });
  });

  const cards = [...document.querySelectorAll(".reveal")];
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add("visible");
      observer.unobserve(entry.target);
    }
  }, { rootMargin: "0px 0px -24px 0px" });
  cards.forEach((card, i) => {
    card.style.setProperty("--delay", `${Math.min(i, 5) * 55}ms`);
    // Was beim Laden im Bild ist, erscheint sofort. Inhalte dürfen nie am Beobachter hängen bleiben.
    if (card.getBoundingClientRect().top < innerHeight) card.classList.add("visible");
    else observer.observe(card);
  });
  setTimeout(() => cards.forEach(card => card.classList.add("visible")), 2500);

  document.querySelectorAll("[data-download]").forEach(link =>
    link.addEventListener("click", () => toast(`Download gestartet: ${link.textContent.trim()}`)));
}

function moveThumb(animate = true) {
  const group = $("views");
  const active = group.querySelector('[aria-pressed="true"]');
  const thumb = group.querySelector(".segmented-thumb");
  if (!animate) thumb.style.transition = "none";
  thumb.style.width = `${active.offsetWidth}px`;
  thumb.style.transform = `translateX(${active.offsetLeft}px)`;
  if (!animate) requestAnimationFrame(() => { thumb.style.transition = ""; });
}

/* ---------- Live ---------- */
let flow;

/** Rollt eine Ziffernfolge wie ein Zählwerk nach oben weiter. */
function rollTo(host, text) {
  if (host.dataset.value === text) return;
  host.dataset.value = text;
  if (reducedMotion() || !host.firstElementChild) {
    host.innerHTML = `<span>${esc(text)}</span>`;
    return;
  }
  host.querySelectorAll(".out").forEach(node => node.remove());
  const old = host.firstElementChild;
  old.className = "out";
  old.addEventListener("animationend", () => old.remove());
  const next = document.createElement("span");
  next.className = "in";
  next.textContent = text;
  host.append(next);
}

/**
 * Live-Anzeige: Das Alter zählt jede Sekunde weiter, der Ring läuft bis zur nächsten Abfrage leer
 * und jede neue Messung löst einen Impuls aus. Läuft die Abfrage über, atmet der Punkt.
 */
const liveTimer = {
  ts: null, poll: 10, offset: 0,
  update(sample, serverNow, poll) {
    this.poll = poll;
    this.offset = serverNow - Date.now() / 1000;
    if (sample && sample.ts !== this.ts) {
      const first = this.ts === null;
      this.ts = sample.ts;
      const ring = $("status-progress");
      ring.classList.add("reset");
      this.draw();
      requestAnimationFrame(() => ring.classList.remove("reset"));
      if (!first && !reducedMotion()) {
        const status = $("status");
        status.classList.remove("pulse");
        void status.offsetWidth;
        status.classList.add("pulse");
      }
    }
    this.draw();
  },
  draw() {
    const status = $("status");
    if (this.ts === null || status.dataset.kind !== "ok") {
      $("status-progress").style.strokeDasharray = status.dataset.kind === "danger" ? "100 100" : "0 100";
      return;
    }
    const age = Math.max(Math.round(Date.now() / 1000 + this.offset - this.ts), 0);
    const remaining = this.poll - age;
    rollTo($("status-age"), String(age));
    $("status-next").textContent = String(Math.max(remaining, 0));
    $("status-progress").style.strokeDasharray = `${Math.max(remaining / this.poll * 100, 0)} 100`;
    status.dataset.waiting = String(remaining < -2);
  },
};

function setStatus(kind, text, showAge = false) {
  const el = $("status");
  el.dataset.kind = kind;
  if (kind !== "ok") el.dataset.waiting = "false";
  $("status-label").textContent = text;
  $("status-time").hidden = !showAge;
  $("status-next-wrap").hidden = !showAge;
}

function setGauge(id, value) {
  const gauge = $(id);
  gauge.querySelector(".gauge-value").style.strokeDasharray = `${Math.max(0, Math.min(value ?? 0, 100))} 100`;
  tween(gauge.querySelector("[data-value]"), value, pct);
}

// Tageswerte aus dem Wechselrichter überbrücken die erste Minute, bevor etwas gespeichert ist.
function todayEnergy(d) {
  if (d.today) return d.today;
  const s = d.sample;
  if (!s) return null;
  return {
    pvKwh: s.todayPvKwh, loadKwh: s.todayLoadKwh, importKwh: s.todayImportKwh, exportKwh: s.todayExportKwh,
    chargeKwh: s.todayChargeKwh, dischargeKwh: s.todayDischargeKwh, balconyKwh: 0,
    autarkyPct: s.todayLoadKwh > 0 ? Math.max((1 - s.todayImportKwh / s.todayLoadKwh) * 100, 0) : null,
    selfConsumptionPct: s.todayPvKwh > 0 ? Math.max((1 - s.todayExportKwh / s.todayPvKwh) * 100, 0) : null,
  };
}

let lastViaPi = null;

async function refreshConnection() {
  const c = await getJson("api/connection");
  document.body.classList.toggle("mirror", c.mirror);
  if (c.mirror) {
    $("source").hidden = false;
    $("source").dataset.mode = "pi";
    const age = Math.max(0, Math.round(Date.now() / 1000 - c.lastPushTs));
    $("source").textContent = !c.lastPushTs ? "Wartet auf den Pi" : age < 120 ? `Stand vor ${age} s` : `Stand vor ${Math.round(age / 60)} min`;
    return;
  }
  $("source").hidden = !c.configured;
  $("source-notice").hidden = !c.configured || c.viaPi;
  if (!c.configured) return;
  $("source").dataset.mode = c.viaPi ? "pi" : "direct";
  $("source").textContent = c.viaPi ? "Über Pi" : "Direkt";
  if (lastViaPi !== null && lastViaPi !== c.viaPi) {
    toast(c.viaPi ? "Der Pi ist wieder da, seine Daten werden angezeigt." : "Pi nicht erreichbar, lese direkt vom Wechselrichter.");
    // Verlauf und Kalender stammen jetzt aus einer anderen Datenbank.
    loadRange({ animate: false });
    loadCalendar(state.calendarYear ?? undefined);
    refreshSparklines();
    refreshPage();
  }
  lastViaPi = c.viaPi;
}

async function refreshLive() {
  let d;
  try {
    await refreshConnection();
    d = await getJson("api/live");
  } catch {
    setStatus("danger", "Server nicht erreichbar");
    liveTimer.draw();
    $("alert").hidden = false;
    $("alert-text").textContent = "Die Verbindung zum Pi ist unterbrochen. Die Anzeige aktualisiert sich automatisch, sobald er wieder erreichbar ist.";
    document.querySelector(".hero").classList.add("stale");
    return;
  }
  state.live = d;
  const stale = d.ageSeconds == null || d.ageSeconds > Math.max(60, d.pollSeconds * 4);
  // Das Balkonkraftwerk läuft am Deye vorbei: Es erzeugt zusätzlich und versorgt das Haus, Netz und Batterie sind schon korrekt gemessen.
  const balconyW = d.balcony?.w ?? 0;
  const sample = stale ? null : { ...d.sample, pvW: d.sample.pvW + balconyW, loadW: d.sample.loadW + balconyW, balconyW };
  const fault = d.sample && (d.sample.state === 3 || d.sample.state === 4);

  if (d.ageSeconds == null) setStatus("wait", "Warte auf Daten");
  else if (stale) setStatus("danger", `Keine Daten seit ${num(d.ageSeconds / 60)} min`);
  else if (fault) setStatus("danger", d.stateName);
  else setStatus("ok", "Live ·", true);
  liveTimer.update(d.sample, d.serverNow, d.pollSeconds);

  const badge = $("alert-count");
  badge.hidden = !d.openAlerts;
  badge.textContent = d.openAlerts;
  badge.dataset.severity = d.worstAlert ?? "";

  $("alert").hidden = !(stale && d.error);
  $("alert-text").textContent = d.error ? `Wechselrichter nicht erreichbar: ${d.error}` : "";
  document.querySelector(".hero").classList.toggle("stale", stale && d.ageSeconds != null);
  if (d.device) $("device").textContent = `Deye ${d.device.serialNumber}`;
  document.title = sample ? `${watt(sample.pvW)} PV · SKx Solar` : "SKx Solar";

  const energy = todayEnergy(d);
  flow.update(sample, energy?.peakPvW ?? 0);

  renderNow(sample, d.balcony?.configured);
  setGauge("gauge-autarky", energy?.autarkyPct);
  setGauge("gauge-self", energy?.selfConsumptionPct);

  tween($("k-pv"), energy?.pvKwh, kwh);
  tween($("k-load"), energy?.loadKwh, kwh);
  tween($("k-import"), energy?.importKwh, kwh);
  tween($("k-export"), energy?.exportKwh, kwh);
  $("k-pv-sub").textContent = [energy?.peakPvW ? `Spitze ${watt(energy.peakPvW)}` : "", energy?.balconyKwh ? `Balkon ${kwh(energy.balconyKwh)}` : ""].filter(Boolean).join(" · ") || " ";
  $("k-load-sub").textContent = energy?.loadKwh && d.sample ? `${watt(d.sample.loadW + balconyW)} aktuell` : " ";
  $("k-import-sub").textContent = energy?.loadKwh ? `${pct(energy.importKwh / energy.loadKwh * 100)} des Verbrauchs` : " ";
  $("k-export-sub").textContent = energy?.pvKwh ? `${pct(energy.exportKwh / energy.pvKwh * 100)} der Erzeugung` : " ";
  $("k-savings-card").hidden = d.todaySavingsEur == null;
  if (d.todaySavingsEur != null) tween($("k-savings"), d.todaySavingsEur, v => `${num(v, 2)} €`);

  renderBalance(energy, d.todayFlows);
  renderSystem();
}

/* ---------- Bilanz ---------- */
function stack(host, title, total, parts, { format = kwh, min = 0.04 } = {}) {
  const used = parts.filter(p => p.value > min);
  const sum = used.reduce((a, p) => a + p.value, 0);
  let root = host.firstElementChild;
  if (!root) {
    host.innerHTML = `<div><div class="balance-title"><span></span><span></span></div><div class="stack"></div><div class="stack-legend"></div></div>`;
    root = host.firstElementChild;
  }
  const [name, value] = root.querySelector(".balance-title").children;
  name.textContent = title;
  value.textContent = format(total);
  const bar = root.querySelector(".stack");
  bar.classList.toggle("stack-empty", !used.length);
  // Segmente bleiben bestehen, damit sich Breiten beim Aktualisieren weich ändern.
  const existing = new Map([...bar.children].map(c => [c.dataset.key, c]));
  for (const [index, p] of parts.entries()) {
    let seg = existing.get(p.key);
    if (p.value > min) {
      if (!seg) {
        seg = document.createElement("i");
        seg.dataset.key = p.key;
        seg.style.setProperty("--c", p.color);
        seg.style.setProperty("--v", 0);
        bar.append(seg);
        requestAnimationFrame(() => requestAnimationFrame(() => seg.style.setProperty("--v", p.value)));
      } else {
        seg.style.setProperty("--v", p.value);
      }
      // Ein später hinzukommender Teil behält seinen festen Platz.
      seg.style.order = index;
      seg.title = `${p.label}: ${format(p.value)}`;
    } else {
      seg?.remove();
    }
  }
  morph(root.querySelector(".stack-legend"), parts.map(p => `
    <div><i style="--c:${p.color}"></i><span>${esc(p.label)}</span><span>${format(p.value)}</span><em>${sum > 0 ? pct(p.value / sum * 100) : "–"}</em></div>`).join(""));
}

/** Der Balkon zählt beim Haus zuerst, das ist eine Rechnung: Gemessen wird nur, was er insgesamt erzeugt. */
function renderNow(sample, balconyConfigured) {
  const flows = sample ? computeFlows(sample) : { pvHome: 0, batHome: 0, gridHome: 0 };
  const balcony = sample?.balconyW ?? 0;
  const balconyHome = Math.min(balcony, flows.pvHome);
  const balconyPart = value => balconyConfigured ? [{ key: "balcony", label: "Balkon", value, color: COLORS.balcony }] : [];
  const options = { format: watt, min: 20 };
  stack($("now-source"), "Das Haus wird versorgt von", sample?.loadW ?? 0, [
    ...balconyPart(balconyHome),
    { key: "pv", label: "Dach", value: flows.pvHome - balconyHome, color: COLORS.pv },
    { key: "bat", label: "Batterie", value: flows.batHome, color: COLORS.battery },
    { key: "grid", label: "Netz", value: flows.gridHome, color: COLORS.grid },
  ], options);
  stack($("now-generation"), "Erzeugung", sample?.pvW ?? 0, [
    { key: "pv", label: "Dach", value: (sample?.pvW ?? 0) - balcony, color: COLORS.pv },
    ...balconyPart(balcony),
  ], options);
}

/** Die Wege rechnet der Server aus den Minutenwerten. Ohne sie, etwa kurz nach Mitternacht, bleibt die einfache Rechnung. */
function renderBalance(e, flows) {
  const v = e || { pvKwh: 0, loadKwh: 0, importKwh: 0, exportKwh: 0, chargeKwh: 0, dischargeKwh: 0, balconyKwh: 0 };
  const f = flows ?? {
    directKwh: Math.max(v.loadKwh - v.importKwh - v.dischargeKwh, 0), batteryHomeKwh: v.dischargeKwh, gridHomeKwh: v.importKwh,
    solarChargeKwh: v.chargeKwh, solarOwnUseKwh: 0,
  };
  const direct = f.directKwh;
  const configured = state.live?.balcony?.configured || v.balconyKwh > 0;
  // Gerechnet wie bei „Gerade jetzt“: Der Balkon deckt das Haus zuerst, gemessen ist nur seine Erzeugung.
  const balconyDirect = Math.min(v.balconyKwh, direct);
  stack($("balance-source"), "Woher kam der Strom?", v.loadKwh, [
    ...(configured ? [{ key: "balcony", label: "Direkt vom Balkon", value: balconyDirect, color: COLORS.balcony }] : []),
    { key: "pv", label: configured ? "Direkt vom Dach" : "Direkt von PV", value: direct - balconyDirect, color: COLORS.pv },
    { key: "bat", label: "Aus der Batterie", value: f.batteryHomeKwh, color: COLORS.battery },
    { key: "grid", label: "Aus dem Netz", value: f.gridHomeKwh, color: COLORS.grid },
  ]);
  $("balance-generation").hidden = !configured;
  if (configured) stack($("balance-generation"), "Woher kam der Solarstrom?", v.pvKwh, [
    { key: "pv", label: "Dach", value: v.pvKwh - v.balconyKwh, color: COLORS.pv },
    { key: "balcony", label: "Balkon", value: v.balconyKwh, color: COLORS.balcony },
  ]);
  stack($("balance-use"), "Wohin ging der Solarstrom?", v.pvKwh, [
    { key: "home", label: "Direkt verbraucht", value: direct, color: COLORS.load },
    { key: "bat", label: "In die Batterie", value: f.solarChargeKwh, color: COLORS.battery },
    { key: "grid", label: "Eingespeist", value: v.exportKwh, color: COLORS.grid },
    { key: "own", label: "Wechselrichter", value: f.solarOwnUseKwh, color: "var(--muted)" },
  ]);
}

/* ---------- Anlage ---------- */
const meter = (label, fraction, value, sub, color) => `
  <div class="meter-row">
    <span>${esc(label)}</span>
    <div class="meter"><i style="--v:${Math.max(0, Math.min(fraction, 1)).toFixed(3)};--c:${color}"></i></div>
    <span>${esc(value)}${sub ? `<small>${esc(sub)}</small>` : ""}</span>
  </div>`;
const fact = (label, value) => `<div class="fact"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;

function renderSystem(swap = false) {
  const panel = $("system-panel");
  const d = state.live;
  const s = d?.sample;
  if (!s) { panel.innerHTML = `<p class="muted">Noch keine Messwerte.</p>`; return; }

  let html;
  if (state.systemTab === "pv") {
    const b = d.balcony;
    const peak = Math.max(s.pv1W, s.pv2W, b?.w ?? 0, 1);
    html = meter("String 1", s.pv1W / peak, watt(s.pv1W), `${num(s.pv1V)} V · ${num(s.pv1A, 1)} A`, COLORS.pv)
      + meter("String 2", s.pv2W / peak, watt(s.pv2W), `${num(s.pv2V)} V · ${num(s.pv2A, 1)} A`, COLORS.pv)
      + (b?.configured ? meter("Balkon", b.w / peak, watt(b.w), b.lastSeenTs ? (d.today?.balconyKwh ? `heute ${kwh(d.today.balconyKwh)}` : "") : "noch keine Verbindung", COLORS.balcony) : "")
      + `<div class="facts" style="margin-top:8px">${fact("Erzeugung gesamt", kwh(s.totalPvKwh))}${fact("Heute laut Deye", kwh(s.todayPvKwh))}</div>`;
  } else if (state.systemTab === "battery") {
    html = meter("Ladestand", s.batterySoc / 100, pct(s.batterySoc), s.batteryW < -20 ? `lädt mit ${watt(-s.batteryW)}` : s.batteryW > 20 ? `entlädt mit ${watt(s.batteryW)}` : "in Ruhe", COLORS.battery)
      + `<div class="facts" style="margin-top:8px">${fact("Spannung", `${num(s.batteryV, 1)} V`)}${fact("Strom", `${num(s.batteryA, 1)} A`)}${fact("Temperatur", `${num(s.batteryTempC, 1)} °C`)}${fact("Geladen gesamt", kwh(s.totalChargeKwh))}${fact("Entladen gesamt", kwh(s.totalDischargeKwh))}</div>`;
  } else if (state.systemTab === "grid") {
    const peak = Math.max(Math.abs(s.loadW1), Math.abs(s.loadW2), Math.abs(s.loadW3), 1);
    html = [1, 2, 3].map(i => meter(`L${i}`, Math.abs(s[`loadW${i}`]) / peak, watt(s[`loadW${i}`]), `${num(s[`gridV${i}`], 1)} V · Netz ${watt(s[`gridW${i}`])}`, COLORS.load)).join("")
      + `<div class="facts" style="margin-top:8px">${fact("Frequenz", `${num(s.gridHz, 2)} Hz`)}${fact("Bezug gesamt", kwh(s.totalImportKwh))}${fact("Einspeisung gesamt", kwh(s.totalExportKwh))}</div>`;
  } else {
    html = `<div class="facts">${fact("Zustand", d.stateName)}${fact("Seriennummer", d.device?.serialNumber ?? "–")}${fact("Wechselrichter", `${num(s.inverterTempC, 1)} °C`)}${fact("DC-Seite", `${num(s.dcTempC, 1)} °C`)}${fact("Verbrauch gesamt", kwh(s.totalLoadKwh))}${fact("Abfrageintervall", `${d.pollSeconds} s`)}</div>`;
  }
  // Nur der Tab-Wechsel blendet ein, Messwert-Updates gleiten im Bestand.
  if (swap) {
    panel.innerHTML = html;
    panel.classList.remove("swap");
    void panel.offsetWidth;
    panel.classList.add("swap");
  } else {
    morph(panel, html);
  }
}

function initTabs() {
  const tabs = [...$("system-tabs").querySelectorAll("button")];
  const select = button => {
    state.systemTab = button.dataset.tab;
    tabs.forEach(t => {
      t.setAttribute("aria-selected", String(t === button));
      t.tabIndex = t === button ? 0 : -1;
    });
    renderSystem(true);
  };
  tabs.forEach((button, i) => {
    button.tabIndex = i === 0 ? 0 : -1;
    button.addEventListener("click", () => select(button));
    button.addEventListener("keydown", e => {
      if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      e.preventDefault();
      e.stopPropagation();
      const next = tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
      next.focus();
      select(next);
    });
  });
}

/* ---------- Zeitraum ---------- */
function setLegend(items) {
  state.legendKeys = items.map(toggleOf);
  morph($("legend"), items.map(s =>
    `<button type="button" data-key="${toggleOf(s)}" aria-pressed="${isShown(s)}" style="--c:${s.color}"><i></i>${esc(s.label)}</button>`).join(""));
}

function setTable(headers, rows) {
  morph($("table-wrap"), `<table><thead><tr>${headers.map(h => `<th scope="col">${esc(h)}</th>`).join("")}</tr></thead>
    <tbody>${rows.map(r => `<tr>${r.map(c => `<td>${esc(c)}</td>`).join("")}</tr>`).join("") || `<tr><td colspan="${headers.length}">Keine Daten</td></tr>`}</tbody></table>`);
}

function setLabel(text) {
  const label = $("range-label");
  if (label.textContent === text) return;
  label.textContent = text;
  label.classList.remove("swap");
  void label.offsetWidth;
  label.classList.add("swap");
}

function summary(e, savings) {
  if (!e) return "Keine Energiedaten für diesen Zeitraum.";
  const parts = [e.balconyKwh > 0 ? `PV ${kwh(e.pvKwh)} (Dach ${kwh(e.pvKwh - e.balconyKwh)}, Balkon ${kwh(e.balconyKwh)})` : `PV ${kwh(e.pvKwh)}`, `Verbrauch ${kwh(e.loadKwh)}`, `Bezug ${kwh(e.importKwh)}`, `Einspeisung ${kwh(e.exportKwh)}`];
  if (savings != null) parts.push(`Ersparnis ${num(savings, 2)} €`);
  return parts.join(" · ");
}

function isCurrent(now = new Date()) {
  if (state.mode === "day") return iso(state.date) === iso(now);
  if (state.mode === "month") return state.date.getFullYear() === now.getFullYear() && state.date.getMonth() === now.getMonth();
  return state.date.getFullYear() === now.getFullYear();
}

/** Auch ein abgebautes Balkonkraftwerk bleibt in seinen alten Zeiträumen sichtbar. */
const hasBalcony = measured => measured || Boolean(state.live?.balcony?.configured);

/** Zeichnet Monat und Jahr. Die Balken hängen an den Schaltern, deshalb entstehen sie bei jedem Zeichnen neu. */
function drawEnergy(items, balcony, options) {
  setLegend(energyLegend(balcony));
  state.draw = first => {
    const { bars, stacked } = energyBars(balcony);
    drawBars($("chart"), items, bars, {
      ...options,
      animate: first,
      extraRow: stacked ? item => tooltip.row("Gesamt", kwh(item.pvKwh), TOTAL_COLOR) : null,
    });
  };
}

async function loadDay(animate, token) {
  const date = iso(state.date);
  const d = await getJson(`api/day?date=${date}`);
  if (token !== loadToken) return;
  $("range-title").textContent = isCurrent() ? "Verlauf heute" : "Tagesverlauf";
  setLabel(state.date.toLocaleDateString("de-DE", { weekday: "short", day: "2-digit", month: "2-digit", year: "numeric" }));
  $("range-summary").textContent = summary(d.energy, d.savingsEur);
  $("soc-wrap").hidden = false;
  $("export-minutes").href = `api/export/minutes.csv?from=${date}&to=${date}`;
  const balcony = hasBalcony(d.points.some(p => p.balconyW > 0));
  const series = daySeries(balcony);
  setLegend(series);

  const start = new Date(state.date); start.setHours(0, 0, 0, 0);
  const end = new Date(start); end.setDate(end.getDate() + 1);
  const range = { start: start.getTime() / 1000, end: end.getTime() / 1000 };
  const live = isCurrent();
  const group = state.cursors ??= cursorGroup();
  state.draw = first => {
    const socSeries = { key: "soc", label: "Ladestand", color: COLORS.battery, format: pct };
    drawTimeline($("chart"), d.points, series.filter(isShown), {
      ...range, cursorGroup: group, live, animate: first, label: "Leistung im Tagesverlauf", format: watt,
      tooltipSeries: [...series.filter(isShown), socSeries],
    });
    drawTimeline($("chart-soc"), d.points, [socSeries], {
      ...range, cursorGroup: group, live, animate: first, label: "Batterieladestand im Tagesverlauf",
      scale: { lo: 0, hi: 100, step: 50 }, format: pct,
    });
  };
  state.draw(animate);
  setTable(["Zeit", "PV", ...(balcony ? ["Dach", "Balkon"] : []), "Verbrauch", "Batterie", "Netz", "Ladestand"], d.points.filter(p => Math.floor(p.t / 60) % 15 === 0).map(p => [
    new Date(p.t * 1000).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" }),
    watt(p.pv), ...(balcony ? [watt(p.roof), watt(p.balconyW)] : []), watt(p.load), watt(p.battery), watt(p.grid), pct(p.soc),
  ]));
}

function openDay(date) {
  state.date = date;
  if (currentPage !== "overview") showPage("overview");
  setMode("day");
  document.querySelector(".range").scrollIntoView({ block: "start", behavior: reducedMotion() ? "auto" : "smooth" });
}

async function loadMonth(animate, token) {
  const y = state.date.getFullYear(), m = state.date.getMonth();
  const d = await getJson(`api/month?month=${y}-${String(m + 1).padStart(2, "0")}`);
  if (token !== loadToken) return;
  $("range-title").textContent = "Monatsübersicht (kWh pro Tag)";
  setLabel(state.date.toLocaleDateString("de-DE", { month: "long", year: "numeric" }));
  $("range-summary").textContent = summary(d.days.length ? d : null, d.savingsEur);
  $("soc-wrap").hidden = true;
  const count = new Date(y, m + 1, 0).getDate();
  const items = Array.from({ length: count }, (_, i) => withRoof(d.days.find(x => Number(x.date.slice(8)) === i + 1)) || null);
  const balcony = hasBalcony(d.balconyKwh > 0);
  drawEnergy(items, balcony, {
    label: "Erzeugung und Verbrauch pro Tag",
    xLabel: i => String(i + 1),
    title: i => new Date(y, m, i + 1).toLocaleDateString("de-DE", { weekday: "short", day: "2-digit", month: "2-digit" }),
    onSelect: i => openDay(new Date(y, m, i + 1)),
  });
  state.draw(animate);
  setTable(["Tag", "PV", ...(balcony ? ["Dach", "Balkon"] : []), "Verbrauch", "Bezug", "Einspeisung", "Autarkie", "Eigenverbrauch"], d.days.map(x => [
    x.date.split("-").reverse().join("."), kwh(x.pvKwh), ...(balcony ? [kwh(x.pvKwh - x.balconyKwh), kwh(x.balconyKwh)] : []), kwh(x.loadKwh), kwh(x.importKwh), kwh(x.exportKwh), pct(x.autarkyPct), pct(x.selfConsumptionPct),
  ]));
}

async function loadYear(animate, token) {
  const y = state.date.getFullYear();
  const d = await getJson(`api/year?year=${y}`);
  if (token !== loadToken) return;
  const names = Array.from({ length: 12 }, (_, i) => new Date(y, i, 1).toLocaleDateString("de-DE", { month: "short" }).replace(".", ""));
  $("range-title").textContent = "Jahresübersicht (kWh pro Monat)";
  setLabel(String(y));
  $("range-summary").textContent = summary(d.months.length ? d : null, d.savingsEur);
  $("soc-wrap").hidden = true;
  const items = names.map((_, i) => withRoof(d.months.find(x => Number(x.period.slice(5)) === i + 1)) || null);
  const balcony = hasBalcony(d.balconyKwh > 0);
  drawEnergy(items, balcony, {
    label: "Erzeugung und Verbrauch pro Monat",
    xLabel: i => names[i],
    title: i => new Date(y, i, 1).toLocaleDateString("de-DE", { month: "long", year: "numeric" }),
    onSelect: i => {
      state.date = new Date(y, i, 1);
      setMode("month");
    },
  });
  state.draw(animate);
  setTable(["Monat", "PV", ...(balcony ? ["Dach", "Balkon"] : []), "Verbrauch", "Bezug", "Einspeisung", "Autarkie", "Eigenverbrauch"], d.months.map(x => [
    names[Number(x.period.slice(5)) - 1], kwh(x.pvKwh), ...(balcony ? [kwh(x.pvKwh - x.balconyKwh), kwh(x.balconyKwh)] : []), kwh(x.loadKwh), kwh(x.importKwh), kwh(x.exportKwh), pct(x.autarkyPct), pct(x.selfConsumptionPct),
  ]));
}

let loadToken = 0;
async function loadRange({ animate = true } = {}) {
  const token = ++loadToken;
  const current = isCurrent();
  $("next").disabled = current;
  $("today").hidden = current;
  const body = $("range-body");
  if (animate) body.classList.add("busy");
  try {
    await ({ day: loadDay, month: loadMonth, year: loadYear }[state.mode])(animate, token);
  } catch {
    if (token === loadToken) $("range-summary").textContent = "Die Daten konnten nicht geladen werden.";
  } finally {
    if (token === loadToken) body.classList.remove("busy");
  }
}

function setMode(mode) {
  state.mode = mode;
  document.querySelectorAll("[data-view]").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.view === mode)));
  moveThumb();
  loadRange();
}

function shift(delta) {
  let d = new Date(state.date);
  const now = new Date();
  if (state.mode === "day") d.setDate(d.getDate() + delta);
  else if (state.mode === "month") { d.setDate(1); d.setMonth(d.getMonth() + delta); }
  else { d.setDate(1); d.setFullYear(d.getFullYear() + delta); }
  // Im Jahr zählt nur das Jahr: Aus Dezember 2025 führt der Schritt auch im November 2026 noch nach 2026.
  if (delta > 0 && (state.mode === "year" ? d.getFullYear() > now.getFullYear() : d > now)) return;
  if (d > now) d = now;
  state.date = d;
  loadRange();
  if (d.getFullYear() !== state.calendarYear) loadCalendar(d.getFullYear());
}

/* ---------- Kalender und Kennzahlen-Kurven ---------- */
async function loadCalendar(year = new Date().getFullYear()) {
  try {
    const days = await getJson(`api/days?from=${year}-01-01&to=${year}-12-31`);
    state.calendarYear = year;
    state.calendarDays = days;
    $("calendar-year").textContent = year;
    renderCalendar();
  } catch { /* Der Kalender ist optional, der nächste Durchlauf versucht es erneut. */ }
}

async function refreshSparklines() {
  try {
    const d = await getJson("api/day");
    state.todayPoints = d.points;
    renderSparklines();
  } catch { /* wie oben */ }
}

function renderSparklines() {
  const p = state.todayPoints;
  drawSparkline($("s-pv"), p, x => x.pv, COLORS.pv);
  drawSparkline($("s-load"), p, x => x.load, COLORS.load);
  drawSparkline($("s-import"), p, x => Math.max(x.grid, 0), COLORS.grid);
  drawSparkline($("s-export"), p, x => Math.max(-x.grid, 0), COLORS.battery);
}

/* ---------- Bereiche ---------- */
const PAGES = { overview: null, analysis: () => renderAnalysis(openDay), planning: renderPlanning, alerts: renderAlerts, system: renderSystemPage };
let currentPage = "overview";

async function refreshPage() {
  try {
    await PAGES[currentPage]?.();
  } catch {
    if (currentPage !== "overview") toast("Die Daten konnten nicht geladen werden.");
  }
}

function showPage(name, push = true) {
  if (!(name in PAGES)) name = "overview";
  currentPage = name;
  document.querySelectorAll("[data-page]").forEach(p => { p.hidden = p.dataset.page !== name; });
  document.querySelectorAll("[data-page-link]").forEach(b => {
    if (b.dataset.pageLink === name) b.setAttribute("aria-current", "page");
    else b.removeAttribute("aria-current");
  });
  if (push && location.hash.slice(1) !== name) history.replaceState(null, "", `#${name}`);
  scrollTo({ top: 0, behavior: "instant" });
  tooltipReset();
  // Diagramme kennen ihre Breite erst, wenn der Bereich sichtbar ist.
  requestAnimationFrame(() => {
    state.draw?.(false);
    moveThumb(false);
    renderCalendar();
    renderSparklines();
    refreshPage();
  });
}

function tooltipReset() { $("tooltip").classList.remove("show"); }

function renderCalendar() {
  if (state.calendarDays && currentPage === "analysis")
    drawCalendar($("calendar"), state.calendarYear, state.calendarDays, text => openDay(new Date(`${text}T12:00`)));
}

/* ---------- Start ---------- */
function init() {
  $("pagenav").addEventListener("click", e => {
    const button = e.target.closest("[data-page-link]");
    if (button) showPage(button.dataset.pageLink);
  });
  if ("serviceWorker" in navigator && isSecureContext) navigator.serviceWorker.register("sw.js").catch(() => {});
  setInterval(() => liveTimer.draw(), 1000);
  initTheme();
  initMicro();
  initTabs();
  flow = new FlowView($("flow"), $("flow-host"));

  document.querySelectorAll("[data-view]").forEach(button => button.addEventListener("click", () => {
    if (button.dataset.view !== state.mode) setMode(button.dataset.view);
  }));
  $("prev").addEventListener("click", () => shift(-1));
  $("next").addEventListener("click", () => shift(1));
  $("today").addEventListener("click", () => {
    state.date = new Date();
    loadRange();
    if (state.date.getFullYear() !== state.calendarYear) loadCalendar();
  });
  $("table-toggle").addEventListener("click", e => {
    const open = $("table-collapse").classList.toggle("open");
    e.currentTarget.setAttribute("aria-pressed", String(open));
  });
  $("legend").addEventListener("click", e => {
    const button = e.target.closest("button[data-key]");
    if (!button) return;
    const key = button.dataset.key;
    // Mindestens eine Linie bleibt sichtbar.
    if (!state.hidden.has(key) && state.legendKeys.every(k => k === key || state.hidden.has(k))) return;
    state.hidden.has(key) ? state.hidden.delete(key) : state.hidden.add(key);
    button.setAttribute("aria-pressed", String(!state.hidden.has(key)));
    saveHidden();
    state.draw?.(false);
  });
  document.addEventListener("keydown", e => {
    if (currentPage !== "overview" || e.target.closest("input, textarea, select, svg, [role=tab]")) return;
    if (e.key === "ArrowLeft") shift(-1);
    if (e.key === "ArrowRight" && !$("next").disabled) shift(1);
  });

  let resize;
  let lastWidth = innerWidth;
  addEventListener("resize", () => {
    // Auf dem Handy löst schon die einklappende Adressleiste das Ereignis aus. Die Diagramme ändern dabei nichts.
    if (innerWidth === lastWidth) return;
    lastWidth = innerWidth;
    clearTimeout(resize);
    resize = setTimeout(() => {
      state.draw?.(false);
      moveThumb(false);
      renderCalendar();
      refreshPage();
    }, 150);
  });

  moveThumb(false);
  renderBalance(null);
  refreshLive();
  refreshSparklines();
  loadRange();
  loadCalendar();
  showPage(location.hash.slice(1) || "overview", false);
  setInterval(refreshLive, 3000);
  let todayText = iso(new Date());
  setInterval(() => {
    // Wer den laufenden Zeitraum ansieht, bleibt auch über Mitternacht darauf.
    const nowText = iso(new Date());
    if (nowText !== todayText) {
      if (isCurrent(new Date(`${todayText}T12:00`))) state.date = new Date();
      todayText = nowText;
    }
    refreshSparklines();
    if (currentPage === "alerts" || currentPage === "system") refreshPage();
    // Nur den laufenden Zeitraum nachladen und dabei nicht erneut animieren.
    if (isCurrent()) loadRange({ animate: false });
    if (state.calendarYear === new Date().getFullYear()) loadCalendar(state.calendarYear);
  }, 60000);
}

init();
