import { esc, iso, kwh, num, pct, reducedMotion, svgNode as el, tooltip, watt } from "./util.js";

/*
 * Diagramme bleiben bestehen und gleiten bei neuen Daten zum neuen Stand. Komplett neu gezeichnet
 * wird nur, wenn sich die Grundlage ändert: anderer Tag, andere Größe oder ein anderes Format.
 */

const UPDATE_MS = 700;
const ease = t => 1 - (1 - t) ** 3;
const lerp = (a, b, t) => a + (b - a) * t;

function niceScale(min, max, ticks) {
  const span = Math.max(max - min, 1);
  const raw = span / ticks;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map(m => m * mag).find(s => s >= raw);
  return { step, lo: Math.floor(min / step) * step, hi: Math.ceil(max / step) * step };
}

function frame(container) {
  container.innerHTML = "";
  const box = container.getBoundingClientRect();
  const w = Math.max(box.width, 240), h = box.height;
  const svg = el("svg", { viewBox: `0 0 ${w} ${h}`, role: "img", tabindex: "0" });
  container.append(svg);
  return { svg, w, h };
}

/** Eine laufende Animation je Diagramm: Neue Daten übernehmen ab dem aktuellen Zwischenstand. */
function animator() {
  let handle = 0;
  return (step, duration = UPDATE_MS) => {
    cancelAnimationFrame(handle);
    if (reducedMotion() || document.hidden) { step(1); return; }
    const start = performance.now();
    const tick = now => {
      const t = Math.min((now - start) / duration, 1);
      step(ease(t));
      if (t < 1) handle = requestAnimationFrame(tick);
    };
    handle = requestAnimationFrame(tick);
  };
}

/** Rasterlinien wandern mit der Skala, wegfallende blenden aus und neue ein. */
function tickLayer(svg, left, right, w, format) {
  const layer = el("g");
  svg.append(layer);
  const ticks = new Map();
  return {
    setValues(scale) {
      const wanted = new Set();
      for (let v = scale.lo; v <= scale.hi + scale.step / 2; v += scale.step) wanted.add(Number(v.toFixed(6)));
      for (const v of wanted) {
        if (ticks.has(v)) continue;
        const group = el("g", { class: "tick" });
        const line = el("line", { class: v === 0 ? "zero" : "gridline", x1: left, x2: w - right });
        const text = el("text", { class: "axis", x: left - 8, "text-anchor": "end" }, format(v));
        group.append(line, text);
        layer.append(group);
        ticks.set(v, { group, line, text, removing: false });
      }
      for (const [v, tick] of ticks) {
        tick.removing = !wanted.has(v);
        tick.group.classList.toggle("tick-out", tick.removing);
      }
    },
    position(y) {
      for (const [v, tick] of ticks) {
        const pos = y(v);
        tick.line.setAttribute("y1", pos);
        tick.line.setAttribute("y2", pos);
        tick.text.setAttribute("y", pos + 4);
      }
    },
    sweep() {
      for (const [v, tick] of ticks) {
        if (!tick.removing) continue;
        tick.group.remove();
        ticks.delete(v);
      }
    },
  };
}

export const cursorGroup = () => {
  const listeners = new Set();
  return {
    add(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    notify(t) { listeners.forEach(fn => fn(t)); },
  };
};

/* ---------- Tagesverlauf ---------- */

/** Liniendiagramm über einen Tag. `cursorGroup` hält Fadenkreuze mehrerer Diagramme synchron. */
export function drawTimeline(container, points, series, opts) {
  const key = [opts.start, opts.end, opts.scale ? "fixed" : "auto", container.clientWidth, container.clientHeight].join("|");
  const existing = container._timeline;
  if (existing && existing.key === key && points.length) {
    existing.update(points, series, opts);
    return;
  }
  existing?.destroy();
  container._timeline = null;
  if (!points.length) {
    const { svg, w, h } = frame(container);
    svg.setAttribute("aria-label", opts.label);
    svg.append(el("text", { class: "chart-empty", x: w / 2, y: h / 2 }, "Für diesen Tag liegen keine Messwerte vor."));
    return;
  }
  container._timeline = createTimeline(container, key, points, series, opts);
}

function createTimeline(container, key, firstPoints, firstSeries, firstOpts) {
  const { svg, w, h } = frame(container);
  const left = 58, right = 12, top = 10, bottom = 24;
  svg.setAttribute("aria-label", firstOpts.label);
  const { start: t0, end: t1 } = firstOpts;
  const x = t => left + (t - t0) / (t1 - t0) * (w - left - right);

  const hourStep = w < 520 ? 6 : 3;
  for (let hour = 0; hour <= 24; hour += hourStep) {
    const date = new Date(t0 * 1000);
    date.setHours(hour, 0, 0, 0);
    svg.append(el("text", {
      class: "axis", x: x(date.getTime() / 1000), y: h - 6,
      "text-anchor": hour === 0 ? "start" : hour === 24 ? "end" : "middle",
    }, `${String(hour).padStart(2, "0")}:00`));
  }

  const defs = el("defs");
  svg.append(defs);
  const ticks = tickLayer(svg, left, right, w, v => opts.format(v));
  const seriesLayer = el("g");
  svg.append(seriesLayer);
  const cross = el("line", { class: "crosshair", y1: top, y2: h - bottom });
  svg.append(cross);

  const run = animator();
  const marks = new Map();
  let opts = firstOpts;
  let points = firstPoints;
  let series = firstSeries;
  let current = { lo: 0, hi: 1, tMax: 0 };
  let cursorPoint = null;

  const yFor = (lo, hi) => v => top + (hi - v) / (hi - lo) * (h - top - bottom);
  const targetScale = () => {
    if (opts.scale) return opts.scale;
    const values = points.flatMap(p => series.map(s => p[s.key]));
    return niceScale(Math.min(0, ...values), Math.max(1, ...values), 5);
  };

  function ensureMark(s, drawIn) {
    let mark = marks.get(s.key);
    if (mark) {
      mark.removing = false;
      mark.group.classList.remove("series-out");
      return mark;
    }
    const group = el("g", { class: `series-group${drawIn ? "" : " series-in"}` });
    let area = null;
    if (s.fill) {
      const id = `fill-${s.key}-${Math.random().toString(36).slice(2, 7)}`;
      const gradient = el("linearGradient", { id, x1: 0, x2: 0, y1: 0, y2: 1 });
      gradient.append(el("stop", { offset: 0, "stop-color": s.color, "stop-opacity": .22 }), el("stop", { offset: 1, "stop-color": s.color, "stop-opacity": 0 }));
      defs.append(gradient);
      area = el("path", { class: `series-area${drawIn ? "" : " on"}`, fill: `url(#${id})` });
      group.append(area);
      if (drawIn) requestAnimationFrame(() => area.classList.add("on"));
    }
    const line = el("path", { class: "series-line", stroke: s.color });
    if (drawIn) {
      line.classList.add("draw");
      line.setAttribute("pathLength", 1);
      line.addEventListener("animationend", () => {
        line.classList.remove("draw");
        line.removeAttribute("pathLength");
      }, { once: true });
    }
    group.append(line);
    const now = el("circle", { class: "now-dot", r: 3.5, fill: s.color, stroke: "var(--surface)", "stroke-width": 2 });
    group.append(now);
    seriesLayer.append(group);
    const dot = el("circle", { class: "chart-dot", r: 5, fill: s.color });
    svg.append(dot);
    mark = { group, area, line, now, dot, series: s, removing: false };
    marks.set(s.key, mark);
    return mark;
  }

  function render(lo, hi, tMax) {
    const y = yFor(lo, hi);
    ticks.position(y);
    const gap = opts.gapSeconds ?? 300;
    const visible = [];
    for (const p of points) {
      if (p.t > tMax) break;
      visible.push(p);
    }
    const next = points[visible.length];
    const prev = visible.at(-1);
    for (const mark of marks.values()) {
      const k = mark.series.key;
      let d = "", areaD = "", previous = null, firstOfSegment = null;
      const base = y(Math.max(lo, 0));
      const closeArea = () => {
        if (mark.area && firstOfSegment !== null && previous !== null)
          areaD += `L${x(previous).toFixed(1)} ${base}Z`;
      };
      for (const p of visible) {
        const px = x(p.t).toFixed(1), py = y(p[k]).toFixed(1);
        if (previous === null || p.t - previous > gap) {
          closeArea();
          d += `M${px} ${py}`;
          if (mark.area) areaD += `M${px} ${base}L${px} ${py}`;
          firstOfSegment = p.t;
        } else {
          d += `L${px} ${py}`;
          if (mark.area) areaD += `L${px} ${py}`;
        }
        previous = p.t;
      }
      let endX = prev ? x(prev.t) : 0, endY = prev ? y(prev[k]) : 0;
      // Die neue Messung wächst aus dem letzten Punkt heraus, statt plötzlich dazustehen.
      if (prev && next && tMax > prev.t && next.t - prev.t <= gap) {
        const f = (tMax - prev.t) / (next.t - prev.t);
        const v = lerp(prev[k], next[k], f);
        endX = x(tMax);
        endY = y(v);
        d += `L${endX.toFixed(1)} ${endY.toFixed(1)}`;
        if (mark.area) areaD += `L${endX.toFixed(1)} ${endY.toFixed(1)}`;
        previous = tMax;
      }
      closeArea();
      mark.line.setAttribute("d", d);
      mark.area?.setAttribute("d", areaD);
      mark.now.setAttribute("cx", endX);
      mark.now.setAttribute("cy", endY);
      mark.now.style.display = opts.live ? "" : "none";
    }
    placeCursor();
  }

  function placeCursor() {
    const visible = cursorPoint ? 1 : 0;
    cross.style.opacity = visible;
    for (const mark of marks.values()) {
      const shown = visible && !mark.removing;
      mark.dot.style.opacity = shown ? 1 : 0;
      if (!shown) continue;
      const y = yFor(current.lo, current.hi);
      mark.dot.setAttribute("cx", x(cursorPoint.t));
      mark.dot.setAttribute("cy", y(cursorPoint[mark.series.key]));
    }
    if (cursorPoint) {
      cross.setAttribute("x1", x(cursorPoint.t));
      cross.setAttribute("x2", x(cursorPoint.t));
    }
  }

  function apply(nextPoints, nextSeries, nextOpts, animated) {
    const lastBefore = points.at(-1)?.t ?? t0;
    points = nextPoints;
    series = nextSeries;
    opts = nextOpts;

    const wanted = new Set(series.map(s => s.key));
    for (const s of series) ensureMark(s, nextOpts.animate && !reducedMotion());
    for (const [k, mark] of marks) {
      if (wanted.has(k)) continue;
      mark.removing = true;
      mark.group.classList.add("series-out");
    }

    const scale = targetScale();
    ticks.setValues(scale);
    const from = { ...current };
    const lastAfter = points.at(-1).t;
    const toMax = lastAfter;
    // Nur echtes Anhängen gleitet. Fehlen plötzlich Punkte, gilt der neue Stand sofort.
    const fromMax = animated && lastBefore <= lastAfter ? Math.max(from.tMax, lastBefore) : toMax;
    const unchanged = from.lo === scale.lo && from.hi === scale.hi && fromMax === toMax;
    if (!animated || unchanged) {
      current = { lo: scale.lo, hi: scale.hi, tMax: toMax };
      render(current.lo, current.hi, current.tMax);
      ticks.sweep();
      return;
    }
    run(t => {
      current = { lo: lerp(from.lo, scale.lo, t), hi: lerp(from.hi, scale.hi, t), tMax: lerp(fromMax, toMax, t) };
      render(current.lo, current.hi, current.tMax);
      if (t === 1) {
        ticks.sweep();
        for (const [k, mark] of marks) {
          if (!mark.removing) continue;
          mark.group.remove();
          mark.dot.remove();
          marks.delete(k);
        }
      }
    });
  }

  const nearest = t => {
    let best = points[0];
    for (const p of points) if (Math.abs(p.t - t) < Math.abs(best.t - t)) best = p;
    return best;
  };
  const unsubscribe = firstOpts.cursorGroup?.add(t => {
    cursorPoint = t == null ? null : nearest(t);
    placeCursor();
  });
  const content = point => {
    const time = new Date(point.t * 1000).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
    const rows = (opts.tooltipSeries || series).map(s => tooltip.row(s.short || s.label, s.format ? s.format(point[s.key]) : opts.format(point[s.key]), s.color));
    return `<b>${time} Uhr</b>${rows.join("")}`;
  };
  const showAt = (point, cx, cy) => {
    if (opts.cursorGroup) opts.cursorGroup.notify(point.t);
    else { cursorPoint = point; placeCursor(); }
    tooltip.show(content(point), cx, cy);
  };
  const clear = () => {
    if (opts.cursorGroup) opts.cursorGroup.notify(null);
    else { cursorPoint = null; placeCursor(); }
    tooltip.hide();
  };

  svg.addEventListener("pointermove", e => {
    const r = svg.getBoundingClientRect();
    const px = (e.clientX - r.left) / r.width * w;
    const t = t0 + (px - left) / (w - left - right) * (t1 - t0);
    const point = nearest(t);
    if (Math.abs(point.t - t) > (opts.gapSeconds ?? 300) * 3) { clear(); return; }
    showAt(point, e.clientX, e.clientY);
  });
  svg.addEventListener("pointerleave", clear);

  // Tastatur: Pfeiltasten wandern durch die Messpunkte, Werte erscheinen wie beim Hover.
  let index = points.length - 1;
  const keyShow = () => {
    index = Math.min(index, points.length - 1);
    const r = svg.getBoundingClientRect();
    showAt(points[index], r.left + x(points[index].t) / w * r.width, r.top + r.height / 2);
  };
  svg.addEventListener("focus", keyShow);
  svg.addEventListener("blur", clear);
  svg.addEventListener("keydown", e => {
    if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
    e.preventDefault();
    e.stopPropagation();
    index = Math.max(0, Math.min(points.length - 1, index + (e.key === "ArrowRight" ? 5 : -5)));
    keyShow();
  });

  const initial = targetScale();
  current = { lo: initial.lo, hi: initial.hi, tMax: firstPoints.at(-1).t };
  apply(firstPoints, firstSeries, firstOpts, false);

  return {
    key,
    update: (p, s, o) => apply(p, s, { ...o, animate: false }, true),
    destroy: () => unsubscribe?.(),
  };
}

/* ---------- Balken ---------- */

export function drawBars(container, items, bars, opts) {
  const key = [items.length, bars.map(b => `${b.key}:${b.col ?? ""}`).join(), opts.label, container.clientWidth, container.clientHeight].join("|");
  if (container._bars?.key === key && items.some(Boolean)) {
    container._bars.update(items, bars, opts);
    return;
  }
  container._bars = null;
  container.classList.remove("has-hover");
  if (!items.some(Boolean)) {
    const { svg, w, h } = frame(container);
    svg.setAttribute("aria-label", opts.label);
    svg.append(el("text", { class: "chart-empty", x: w / 2, y: h / 2 }, "Für diesen Zeitraum liegen keine Messwerte vor."));
    return;
  }
  container._bars = createBars(container, key, items, bars, opts);
}

function createBars(container, key, firstItems, bars, firstOpts) {
  const { svg, w, h } = frame(container);
  const left = 44, right = 8, top = 10, bottom = 24;
  svg.setAttribute("aria-label", firstOpts.label);
  let items = firstItems;
  let opts = firstOpts;
  const run = animator();
  const ticks = tickLayer(svg, left, right, w, v => num(v));

  const count = items.length;
  const band = (w - left - right) / count;
  const gap = 2;
  // Balken mit gleichem col stapeln sich, alle anderen stehen nebeneinander.
  const columns = [];
  const columnOf = bars.map(b => {
    const found = b.col == null ? -1 : columns.indexOf(b.col);
    if (found >= 0) return found;
    columns.push(b.col ?? b.key);
    return columns.length - 1;
  });
  const columnCount = Math.max(1, columns.length);
  const isTop = bars.map((_, n) => !columnOf.slice(n + 1).includes(columnOf[n]));
  const barWidth = Math.max(2, Math.min(22, (band * .78 - gap * (columnCount - 1)) / columnCount));
  const labelEvery = band < 24 ? (band < 12 ? 5 : 2) : 1;
  const grow = opts.animate && !reducedMotion();
  const valueOf = (item, b) => item ? item[b.key] : 0;
  const columnSums = item => columns.map((_, c) => bars.reduce((sum, b, n) => columnOf[n] === c ? sum + item[b.key] : sum, 0));
  const maxOf = () => niceScale(0, Math.max(1, ...items.filter(Boolean).flatMap(columnSums)), 5);

  const groups = [];
  for (let i = 0; i < count; i++) {
    const cx = left + band * (i + .5);
    if ((i + 1) % labelEvery === 0 || i === 0)
      svg.append(el("text", { class: "axis", x: cx, y: h - 6, "text-anchor": "middle" }, opts.xLabel(i)));
    const group = el("g", { class: "bar-group" });
    group.append(el("rect", { x: left + band * i, y: top, width: band, height: h - top - bottom, fill: "transparent" }));
    const paths = bars.map(b => {
      const path = el("path", { class: `bar${grow ? " grow" : ""}`, fill: b.color, style: `--i:${i}` });
      group.append(path);
      return path;
    });
    const valueText = v => (opts.format ?? kwh)(v);
    const content = () => {
      const item = items[i];
      return `<b>${esc(opts.title(i))}</b>${bars.map(b => tooltip.row(b.label, valueText(item[b.key]), b.color)).join("")}${opts.extraRow?.(item) ?? ""}${item.autarkyPct != null ? tooltip.row("Autarkie", pct(item.autarkyPct)) : ""}`;
    };
    group.addEventListener("pointerenter", () => { if (items[i]) container.classList.add("has-hover"); });
    group.addEventListener("pointermove", e => { if (items[i]) tooltip.show(content(), e.clientX, e.clientY); });
    group.addEventListener("pointerleave", () => { container.classList.remove("has-hover"); tooltip.hide(); });
    const open = () => { if (items[i]) opts.onSelect?.(i); };
    group.addEventListener("click", open);
    group.addEventListener("keydown", e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); open(); } });
    group.addEventListener("focus", () => {
      if (!items[i]) return;
      const r = group.getBoundingClientRect();
      tooltip.show(content(), r.right, r.top + r.height / 2);
    });
    group.addEventListener("blur", () => tooltip.hide());
    svg.append(group);
    groups.push({ group, paths, cx });
  }

  let current = groups.map(() => bars.map(() => 0));
  let hi = 1;

  function render(valueRows, top0) {
    const y = v => top + (top0 - v) / top0 * (h - top - bottom);
    ticks.position(y);
    groups.forEach((g, i) => {
      const bases = columns.map(() => 0);
      bars.forEach((b, n) => {
        const value = valueRows[i][n];
        const column = columnOf[n];
        const base = bases[column];
        bases[column] += value;
        const bx = g.cx - (columnCount * barWidth + gap * (columnCount - 1)) / 2 + column * (barWidth + gap);
        const by = y(base + value);
        // Die Lücke trennt gestapelte Teile, nur der oberste bekommt runde Ecken.
        const bh = Math.max(y(base) - by - (base > 0 ? 2 : 0), value > 0 ? 1 : 0);
        const r = isTop[n] ? Math.min(4, barWidth / 2, bh) : 0;
        g.paths[n].setAttribute("d", `M${bx} ${by + bh}V${by + r}Q${bx} ${by} ${bx + r} ${by}H${bx + barWidth - r}Q${bx + barWidth} ${by} ${bx + barWidth} ${by + r}V${by + bh}Z`);
      });
    });
  }

  function syncMeta() {
    groups.forEach((g, i) => {
      const item = items[i];
      g.group.classList.toggle("bar-empty", !item);
      if (item) {
        g.group.setAttribute("tabindex", "0");
        g.group.setAttribute("role", "button");
        g.group.setAttribute("aria-label", `${opts.title(i)}: ${bars.map(b => `${b.label} ${(opts.format ?? kwh)(item[b.key])}`).join(", ")}`);
      } else {
        g.group.removeAttribute("tabindex");
        g.group.removeAttribute("role");
        g.group.removeAttribute("aria-label");
      }
    });
  }

  function apply(nextItems, nextOpts, animated) {
    items = nextItems;
    opts = nextOpts;
    syncMeta();
    const scale = maxOf();
    ticks.setValues(scale);
    const target = groups.map((_, i) => bars.map(b => valueOf(items[i], b)));
    const fromRows = current;
    const fromHi = hi;
    const same = fromHi === scale.hi && target.every((row, i) => row.every((v, n) => v === fromRows[i][n]));
    if (!animated || same) {
      current = target;
      hi = scale.hi;
      render(current, hi);
      ticks.sweep();
      return;
    }
    run(t => {
      current = target.map((row, i) => row.map((v, n) => lerp(fromRows[i][n], v, t)));
      hi = lerp(fromHi, scale.hi, t);
      render(current, hi);
      if (t === 1) ticks.sweep();
    });
  }

  apply(firstItems, firstOpts, false);
  return { key, update: (next, _bars, o) => apply(next, o, true) };
}

/* ---------- Kennzahl-Kurven ---------- */

const SPARK_SLOTS = 96;

/** Fasst den Tag in Viertelstunden zusammen. So bleibt die Kurve ruhig und neue Werte verschieben sie nicht. */
function sparkSlots(points, value) {
  if (!points.length) return [];
  const start = new Date(points[0].t * 1000);
  start.setHours(0, 0, 0, 0);
  const t0 = start.getTime() / 1000;
  const sums = new Array(SPARK_SLOTS).fill(0), counts = new Array(SPARK_SLOTS).fill(0);
  let last = 0;
  for (const p of points) {
    const slot = Math.min(Math.max(Math.floor((p.t - t0) / 900), 0), SPARK_SLOTS - 1);
    sums[slot] += value(p);
    counts[slot]++;
    last = Math.max(last, slot);
  }
  const out = [];
  let previous = 0;
  for (let i = 0; i <= last; i++) {
    previous = counts[i] ? sums[i] / counts[i] : previous;
    out.push(previous);
  }
  return out;
}

export function drawSparkline(container, points, value, color) {
  const target = sparkSlots(points, value);
  if (target.length < 2) return;
  const w = 200, h = 44;
  let chart = container._spark;
  if (!chart) {
    const svg = el("svg", { viewBox: `0 0 ${w} ${h}`, preserveAspectRatio: "none" });
    const id = `spark-${Math.random().toString(36).slice(2, 7)}`;
    const defs = el("defs");
    const gradient = el("linearGradient", { id, x1: 0, x2: 0, y1: 0, y2: 1 });
    gradient.append(el("stop", { offset: 0, "stop-color": color, "stop-opacity": .24 }), el("stop", { offset: 1, "stop-color": color, "stop-opacity": 0 }));
    defs.append(gradient);
    const area = el("path", { fill: `url(#${id})` });
    const line = el("path", { fill: "none", stroke: color, "stroke-width": 1.5 });
    svg.append(defs, area, line);
    container.innerHTML = "";
    container.append(svg);
    chart = container._spark = { area, line, values: target.map(() => 0), max: Math.max(...target, 1), run: animator() };
  }
  const fromValues = chart.values;
  const fromMax = chart.max;
  const toMax = Math.max(...target, 1);
  const lastFrom = fromValues.at(-1) ?? 0;
  const paint = (vals, max) => {
    const pts = vals.map((v, i) => [i / (SPARK_SLOTS - 1) * w, h - 3 - v / max * (h - 10)]);
    const d = pts.map((p, i) => `${i ? "L" : "M"}${p[0].toFixed(1)} ${p[1].toFixed(1)}`).join("");
    chart.line.setAttribute("d", d);
    chart.area.setAttribute("d", `${d}L${pts.at(-1)[0].toFixed(1)} ${h}L0 ${h}Z`);
  };
  chart.run(t => {
    const vals = target.map((v, i) => lerp(i < fromValues.length ? fromValues[i] : lastFrom, v, t));
    chart.values = vals;
    chart.max = lerp(fromMax, toMax, t);
    paint(vals, chart.max);
  }, 900);
}

/* ---------- Kalender ---------- */

/** Jahreskalender. Fünf Stufen derselben Farbe, die Skala richtet sich nach dem besten Tag. */
export function drawCalendar(container, year, days, onSelect) {
  const available = container.clientWidth;
  let cal = container._calendar;
  if (!cal || cal.year !== year || cal.available !== available) cal = container._calendar = createCalendar(container, year, available);
  cal.onSelect = onSelect;

  const byDate = new Map(days.map(d => [d.date, d]));
  const max = Math.max(1, ...days.map(d => d.pvKwh));
  const steps = [18, 38, 58, 78, 100];
  const todayText = iso(new Date());
  for (const [text, cell] of cal.cells) {
    const day = byDate.get(text) ?? null;
    cell._day = day;
    cell.classList.toggle("data", !!day);
    cell.classList.toggle("empty", !day && text <= todayText);
    cell.classList.toggle("future", !day && text > todayText);
    cell.classList.toggle("is-today", text === todayText);
    if (day) {
      const level = Math.min(Math.floor(day.pvKwh / max * 5), 4);
      cell.style.fill = `color-mix(in srgb, var(--series-pv) ${steps[level]}%, var(--surface-2))`;
    } else {
      cell.style.fill = "";
    }
  }
  if (!cal.shown) {
    cal.shown = true;
    if (cal.width > available) container.scrollLeft = cal.width;
  }
}

function createCalendar(container, year, available) {
  container.innerHTML = "";
  const left = 26, top = 18, gap = 3;
  const first = new Date(year, 0, 1);
  const offset = (first.getDay() + 6) % 7;
  const weeks = Math.ceil((offset + (new Date(year, 11, 31) - first) / 864e5 + 1) / 7);
  const size = Math.max(11, Math.min(22, (available - left - 2) / weeks - gap));
  const step = size + gap;
  const width = left + weeks * step, height = top + 7 * step;
  const svg = el("svg", { width, height, viewBox: `0 0 ${width} ${height}`, role: "img", "aria-label": `Tageserzeugung ${year}` });
  ["Mo", "Mi", "Fr"].forEach((label, i) => svg.append(el("text", { class: "axis", x: 0, y: top + (i * 2) * step + size - 2 }, label)));

  const cal = { year, available, width, cells: new Map(), shown: false, onSelect: null };
  let index = 0;
  for (let date = new Date(first); date.getFullYear() === year; date.setDate(date.getDate() + 1), index++) {
    const position = offset + index;
    const week = Math.floor(position / 7), weekday = position % 7;
    if (date.getDate() === 1)
      svg.append(el("text", { class: "axis", x: left + week * step, y: 11 }, date.toLocaleDateString("de-DE", { month: "short" }).replace(".", "")));
    const text = iso(date);
    const cell = el("rect", { x: left + week * step, y: top + weekday * step, width: size, height: size, class: "cal-cell", style: `--i:${week * 7 + weekday}` });
    cell.dataset.date = text;
    svg.append(cell);
    cal.cells.set(text, cell);
  }
  // Die Einblendung läuft nur einmal je Jahr, nicht bei jeder Breitenänderung.
  if (container._calendarIntro !== year) {
    container._calendarIntro = year;
    svg.classList.add("cal-intro");
    setTimeout(() => svg.classList.remove("cal-intro"), 1200);
  }

  svg.addEventListener("pointermove", e => {
    const day = e.target._day;
    if (!day) { tooltip.hide(); return; }
    const title = new Date(`${day.date}T12:00`).toLocaleDateString("de-DE", { weekday: "long", day: "2-digit", month: "long" });
    tooltip.show(`<b>${esc(title)}</b>${tooltip.row("Erzeugung", kwh(day.pvKwh))}${tooltip.row("Verbrauch", kwh(day.loadKwh))}${day.autarkyPct != null ? tooltip.row("Autarkie", pct(day.autarkyPct)) : ""}`, e.clientX, e.clientY);
  });
  svg.addEventListener("pointerleave", () => tooltip.hide());
  svg.addEventListener("click", e => {
    const day = e.target._day;
    if (!day) return;
    tooltip.hide();
    cal.onSelect(day.date);
  });
  container.append(svg);
  return cal;
}

export { watt };
