import { esc, pct, reducedMotion, svgNode, tooltip, tween, watt } from "./util.js";

const THRESHOLD_W = 20;

/**
 * Teilt die vier Messwerte auf konkrete Stromwege auf: Das Haus wird zuerst aus PV versorgt, dann aus der Batterie
 * und zuletzt aus dem Netz. Vorzeichen: Batterie negativ = laden, Netz positiv = Bezug.
 */
export function computeFlows({ pvW, loadW, batteryW, gridW }) {
  const pv = Math.max(pvW, 0), load = Math.max(loadW, 0);
  const charge = Math.max(-batteryW, 0), discharge = Math.max(batteryW, 0);
  const imported = Math.max(gridW, 0), exported = Math.max(-gridW, 0);

  const pvHome = Math.min(pv, load);
  const batHome = Math.min(discharge, load - pvHome);
  const gridHome = Math.min(imported, load - pvHome - batHome);
  const pvBat = Math.min(pv - pvHome, charge);
  const gridBat = Math.min(charge - pvBat, imported - gridHome);
  const pvGrid = Math.min(pv - pvHome - pvBat, exported);
  const batGrid = Math.min(discharge - batHome, exported - pvGrid);

  return { pvHome, batHome, gridHome, pvBat, gridBat, pvGrid, batGrid };
}

const LAYOUTS = {
  wide: { w: 720, h: 500, r: 56, pv: [360, 64], battery: [108, 232], home: [612, 232], grid: [360, 400] },
  narrow: { w: 360, h: 500, r: 46, pv: [180, 54], battery: [58, 232], home: [302, 232], grid: [180, 400] },
};

const ICONS = {
  pv: `<g class="sun-rays"><path d="M0-15v-3M0 15v3M-15 0h-3M15 0h3M-10.6-10.6l-2.1-2.1M10.6 10.6l2.1 2.1M-10.6 10.6l-2.1 2.1M10.6-10.6l2.1-2.1"/></g><circle r="7"/>`,
  battery: `<rect x="-14" y="-8" width="25" height="16" rx="3"/><path d="M14.5-3v6"/><path class="bolt" d="M-.5-6-5 1h4L-2.500 6 3-1h-4Z"/>`,
  home: `<path d="M-14-1 0-13l14 12"/><path d="M-10-4V12h20V-4"/><path d="M-3 12V4h6v8"/>`,
  grid: `<path d="M-10 14 0-14l10 28"/><path d="M-13-7h26M-10-1h20M-6.500 5l13 5M6.500 5l-13 5"/>`,
};

// Stromwege von Quelle zu Ziel, jeweils mit Serienfarbe der Quelle.
const LINKS = [
  { key: "pvHome", from: "pv", to: "home", curve: "to", color: "var(--series-pv)", label: "PV zum Haus" },
  { key: "pvBat", from: "pv", to: "battery", curve: "to", color: "var(--series-pv)", label: "PV zur Batterie" },
  { key: "pvGrid", from: "pv", to: "grid", color: "var(--series-pv)", label: "PV ins Netz" },
  { key: "batHome", from: "battery", to: "home", color: "var(--series-battery)", label: "Batterie zum Haus" },
  { key: "gridHome", from: "grid", to: "home", curve: "to", color: "var(--series-grid)", label: "Netz zum Haus" },
  { key: "gridBat", from: "grid", to: "battery", curve: "to", color: "var(--series-grid)", label: "Netz zur Batterie" },
  { key: "batGrid", from: "battery", to: "grid", curve: "from", color: "var(--series-battery)", label: "Batterie ins Netz" },
];

const NODE_COLORS = {
  pv: "var(--series-pv)", battery: "var(--series-battery)", home: "var(--series-load)", grid: "var(--series-grid)",
};
const NODE_TITLES = { pv: "PV", battery: "Batterie", home: "Haus", grid: "Netz" };
const PARTICLES_PER_LINK = 7;

export class FlowView {
  constructor(svg, host) {
    this.svg = svg;
    this.host = host;
    this.sample = null;
    this.flows = {};
    this.peak = 0;
    this.layoutName = null;
    this.running = false;
    new ResizeObserver(() => this.layout()).observe(host);
    document.addEventListener("visibilitychange", () => this.start());
    this.layout();
  }

  layout() {
    const name = this.host.clientWidth < 520 ? "narrow" : "wide";
    if (name === this.layoutName) return;
    this.layoutName = name;
    this.build(LAYOUTS[name]);
    this.update(this.sample, this.peak);
  }

  build(L) {
    const svg = this.svg;
    svg.innerHTML = "";
    svg.setAttribute("viewBox", `0 0 ${L.w} ${L.h}`);
    this.links = {};
    this.nodes = {};

    const linkLayer = svgNode("g");
    const particleLayer = svgNode("g");
    const nodeLayer = svgNode("g");
    svg.append(linkLayer, particleLayer, nodeLayer);

    for (const link of LINKS) {
      const a = L[link.from], b = L[link.to];
      // Bögen laufen über die Ecke des Ziels bzw. der Quelle, gerade Wege direkt.
      const d = link.curve === "to"
        ? `M${a[0]} ${a[1]}Q${b[0]} ${a[1]} ${b[0]} ${b[1]}`
        : link.curve === "from"
          ? `M${a[0]} ${a[1]}Q${a[0]} ${b[1] + 34} ${b[0]} ${b[1]}`
          : `M${a[0]} ${a[1]}L${b[0]} ${b[1]}`;
      const path = svgNode("path", { class: "flow-link", d });
      const hit = svgNode("path", { class: "flow-hit", d });
      linkLayer.append(path, hit);

      const length = path.getTotalLength();
      const samples = [];
      for (let i = 0; i <= 80; i++) {
        const p = path.getPointAtLength(length * i / 80);
        samples.push([p.x, p.y]);
      }
      const particles = [];
      for (let i = 0; i < PARTICLES_PER_LINK; i++) {
        const halo = svgNode("circle", { class: "flow-particle", r: 7, fill: link.color, opacity: 0 });
        const dot = svgNode("circle", { class: "flow-particle", r: 3, fill: link.color, opacity: 0 });
        particleLayer.append(halo, dot);
        particles.push({ halo, dot, offset: i / PARTICLES_PER_LINK });
      }
      const entry = { ...link, path, samples, length, particles, power: 0, phase: Math.random() };
      this.links[link.key] = entry;
      hit.addEventListener("pointermove", e => {
        tooltip.show(`<b>${esc(link.label)}</b>${tooltip.row("Leistung", watt(entry.power))}`, e.clientX, e.clientY);
      });
      hit.addEventListener("pointerleave", () => tooltip.hide());
    }

    for (const key of Object.keys(NODE_TITLES)) {
      const [x, y] = L[key];
      const g = svgNode("g", { class: "flow-node", transform: `translate(${x} ${y})` });
      const body = svgNode("g", { class: "flow-node-body" });
      const scale = L.r / 56;
      body.innerHTML = `
        <circle class="flow-node-bg" r="${L.r}"/>
        <circle class="flow-ring-track" r="${L.r - 5}"/>
        <circle class="flow-ring" r="${L.r - 5}" pathLength="100" stroke="${NODE_COLORS[key]}"/>
        <g class="flow-icon" transform="translate(0 ${-15 * scale}) scale(${Math.max(scale, .85)})">${ICONS[key]}</g>
        <text class="flow-value" y="${22 * scale + 2}"></text>`;
      g.append(body);
      const title = svgNode("text", { class: "flow-title", y: L.r + 20 }, NODE_TITLES[key]);
      const sub = svgNode("text", { class: "flow-sub", y: L.r + 36 });
      g.append(title, sub);
      nodeLayer.append(g);
      this.nodes[key] = { g, value: body.querySelector(".flow-value"), ring: body.querySelector(".flow-ring"), sub };
    }
    this.start();
  }

  update(sample, peakPv) {
    this.sample = sample;
    this.peak = peakPv || 0;
    const flows = sample ? computeFlows(sample) : {};
    this.flows = flows;

    for (const entry of Object.values(this.links)) {
      const power = flows[entry.key] || 0;
      entry.power = power;
      const on = power > THRESHOLD_W;
      entry.path.classList.toggle("active", on);
      entry.path.style.stroke = on ? entry.color : "";
      // Dieser seltene Weg verläuft fast parallel zu Netz-Batterie und zeigt sich nur, wenn er wirklich genutzt wird.
      if (entry.key === "batGrid") entry.path.style.visibility = on ? "" : "hidden";
      entry.path.style.strokeWidth = on ? `${Math.min(1.5 + Math.sqrt(power) / 14, 5)}px` : "";
      entry.count = on ? Math.min(Math.ceil(power / 300) + 1, PARTICLES_PER_LINK) : 0;
      entry.speed = on ? Math.min(70 + power / 25, 190) : 0;
    }

    const n = this.nodes;
    const set = (key, power, ring, sub, flags = {}) => {
      tween(n[key].value, sample ? Math.abs(power) : null, v => watt(v));
      n[key].ring.style.strokeDasharray = `${Math.max(0, Math.min(ring, 100))} 100`;
      n[key].sub.textContent = sub;
      n[key].g.classList.toggle("on", !!sample && Math.abs(power) > THRESHOLD_W);
      n[key].g.classList.toggle("charging", !!flags.charging);
    };

    if (!sample) {
      for (const key of Object.keys(n)) set(key, 0, 0, "");
      return;
    }
    const peak = Math.max(this.peak, sample.pvW, 1);
    const covered = sample.loadW > 0 ? (flows.pvHome + flows.batHome) / sample.loadW * 100 : 0;
    set("pv", sample.pvW, sample.pvW / peak * 100, sample.balconyW > THRESHOLD_W ? `davon Balkon ${watt(sample.balconyW)}` : sample.pvW > THRESHOLD_W ? "erzeugt" : "keine Erzeugung");
    set("battery", sample.batteryW, sample.batterySoc,
      `${pct(sample.batterySoc)} · ${sample.batteryW < -THRESHOLD_W ? "lädt" : sample.batteryW > THRESHOLD_W ? "entlädt" : "Ruhe"}`,
      { charging: sample.batteryW < -THRESHOLD_W });
    set("home", sample.loadW, covered, "Verbrauch");
    set("grid", sample.gridW, 0, sample.gridW > THRESHOLD_W ? "Bezug" : sample.gridW < -THRESHOLD_W ? "Einspeisung" : "ausgeglichen");
    this.draw(performance.now());
  }

  start() {
    if (this.running || document.hidden) return;
    this.running = true;
    this.last = performance.now();
    requestAnimationFrame(t => this.tick(t));
  }

  tick(now) {
    if (document.hidden) { this.running = false; return; }
    const dt = Math.min((now - this.last) / 1000, .1);
    this.last = now;
    for (const e of Object.values(this.links || {})) {
      if (e.speed) e.phase = (e.phase + e.speed * dt / e.length) % 1;
    }
    this.draw(now);
    requestAnimationFrame(t => this.tick(t));
  }

  draw() {
    const still = reducedMotion();
    for (const e of Object.values(this.links || {})) {
      e.particles.forEach((p, i) => {
        const visible = !still && i < (e.count || 0);
        if (!visible) {
          if (p.shown) { p.dot.setAttribute("opacity", 0); p.halo.setAttribute("opacity", 0); p.shown = false; }
          return;
        }
        const t = (e.phase + i / e.count) % 1;
        const point = e.samples[Math.round(t * (e.samples.length - 1))];
        // Partikel blenden an den Knoten weich ein und aus.
        const fade = Math.min(Math.min(t, 1 - t) * 8, 1);
        p.dot.setAttribute("cx", point[0]); p.dot.setAttribute("cy", point[1]);
        p.halo.setAttribute("cx", point[0]); p.halo.setAttribute("cy", point[1]);
        p.dot.setAttribute("opacity", fade);
        p.halo.setAttribute("opacity", fade * .22);
        p.shown = true;
      });
    }
  }
}
