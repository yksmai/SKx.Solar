export const $ = id => document.getElementById(id);
export const reducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;

const de = (v, digits = 0) => Number(v).toLocaleString("de-DE", { minimumFractionDigits: digits, maximumFractionDigits: digits });
export const num = (v, digits = 0) => v == null || Number.isNaN(v) ? "–" : de(v, digits);
export const watt = v => v == null ? "–" : Math.abs(v) >= 1000 ? `${de(v / 1000, 2)} kW` : `${de(v)} W`;
export const kwh = v => v == null ? "–" : `${de(v, 1)} kWh`;
export const pct = v => v == null ? "–" : `${de(v)} %`;
export const iso = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
export const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

export async function getJson(url) {
  const response = await fetch(url, { cache: "no-store" });
  // Abgelaufene Anmeldung: zurück zur Anmeldeseite statt scheinbarem Verbindungsfehler.
  if (response.status === 401) location.href = "login.html";
  if (!response.ok) throw new Error(`${url}: ${response.status}`);
  return response.json();
}

/** Zählt eine Anzeige weich zum neuen Wert, statt sie springen zu lassen. */
export function tween(el, value, format, duration = 700) {
  if (value == null) {
    cancelAnimationFrame(el._raf);
    el._value = null;
    el.textContent = format(null);
    return;
  }
  const from = el._value;
  el._value = value;
  if (from == null || from === value || reducedMotion() || document.hidden) {
    cancelAnimationFrame(el._raf);
    el.textContent = format(from == null && !reducedMotion() ? value : value);
    return;
  }
  const start = performance.now();
  cancelAnimationFrame(el._raf);
  const step = now => {
    const t = Math.min((now - start) / duration, 1);
    const eased = 1 - (1 - t) ** 3;
    el.textContent = format(from + (value - from) * eased);
    if (t < 1) el._raf = requestAnimationFrame(step);
  };
  el._raf = requestAnimationFrame(step);
}

function syncChildren(target, source) {
  const next = [...source.childNodes];
  next.forEach((node, i) => {
    const existing = target.childNodes[i];
    if (!existing) target.append(node);
    else if (existing.nodeType !== node.nodeType || existing.nodeName !== node.nodeName) existing.replaceWith(node);
    else if (node.nodeType !== Node.ELEMENT_NODE) {
      if (existing.nodeValue !== node.nodeValue) existing.nodeValue = node.nodeValue;
    } else {
      for (const attr of [...existing.attributes]) if (!node.hasAttribute(attr.name)) existing.removeAttribute(attr.name);
      for (const attr of [...node.attributes]) if (existing.getAttribute(attr.name) !== attr.value) existing.setAttribute(attr.name, attr.value);
      syncChildren(existing, node);
    }
  });
  while (target.childNodes.length > next.length) target.lastChild.remove();
}

/** Ändert im Bestand nur, was anders ist: Balken gleiten, Fokus und Rollposition bleiben erhalten. */
export function morph(host, html) {
  const template = document.createElement("template");
  template.innerHTML = html;
  syncChildren(host, template.content);
}

export const svgNode =(tag, attrs = {}, text) => {
  const node = document.createElementNS("http://www.w3.org/2000/svg", tag);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  if (text != null) node.textContent = text;
  return node;
};

export const tooltip = {
  el: null,
  show(html, x, y) {
    this.el ??= $("tooltip");
    if (this.html !== html) { this.el.innerHTML = html; this.html = html; }
    this.el.classList.add("show");
    const r = this.el.getBoundingClientRect();
    const left = x + 16 + r.width > innerWidth - 8 ? x - r.width - 16 : x + 16;
    const top = Math.max(8, Math.min(y - r.height / 2, innerHeight - r.height - 8));
    this.el.style.transform = `translate(${Math.max(8, left)}px, ${top}px)`;
  },
  hide() {
    this.el ??= $("tooltip");
    this.el.classList.remove("show");
  },
  row(label, value, color) {
    return `<div><span>${color ? `<i style="background:${color}"></i>` : ""}${esc(label)}</span><strong>${esc(value)}</strong></div>`;
  },
};

export function toast(text) {
  const node = document.createElement("div");
  node.className = "toast";
  node.textContent = text;
  $("toasts").append(node);
  setTimeout(() => {
    node.classList.add("out");
    setTimeout(() => node.remove(), 260);
  }, 2800);
}
