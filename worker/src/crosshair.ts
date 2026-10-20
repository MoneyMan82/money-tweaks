// Crosshair rules and preview rendering. Port of src/MoneyTweaks.Core/Crosshair (CrosshairModel defaults,
// CrosshairSanitizer, CrosshairGeometry): a shared crosshair is clamped exactly like the app clamps imports, and
// previews use the same shapes and pixel snapping as the app's overlay.

import { encodePng } from "./png";

type Kind = "Lines" | "Dot" | "Circle" | "Image";
export interface Layer {
  id: string; name: string; kind: Kind; visible: boolean; color: string; opacity: number; thickness: number; length: number; gap: number;
  top: boolean; bottom: boolean; left: boolean; right: boolean; roundEnds: boolean; size: number; dotShape: "Square" | "Round";
  outline: boolean; outlineColor: string; outlineThickness: number; offsetX: number; offsetY: number; rotation: number;
  reactsToFiring: boolean; imageFile: string | null; imageAspect: number;
}
export interface Firing {
  enabled: boolean; trigger: "LeftMouse" | "RightMouse" | "EitherMouse"; spread: number; riseMs: number; recoverMs: number;
  firingOpacity: number; flash: boolean; flashColor: string; hideWhileAiming: boolean;
}
export interface Profile { id: string; name: string; layers: Layer[]; firing: Firing; pixelPerfect: boolean; created: string; modified: string }

const KINDS: Kind[] = ["Lines", "Dot", "Circle", "Image"];
const MAX_LAYERS = 64, MAX_NAME = 60;
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const clamp = (v: unknown, lo: number, hi: number, fallback: number) => (typeof v === "number" && Number.isFinite(v) ? Math.min(hi, Math.max(lo, v)) : fallback);
const bool = (v: unknown, fallback: boolean) => (typeof v === "boolean" ? v : fallback);
const newId = () => crypto.randomUUID().replace(/-/g, "").slice(0, 10);

export function parseArgb(s: unknown): [number, number, number, number] | null {
  if (typeof s !== "string") return null;
  let h = s.trim().replace(/^#/, "");
  if (h.length === 6) h = "FF" + h;
  if (!/^[0-9a-fA-F]{8}$/.test(h)) return null;
  const v = parseInt(h, 16) >>> 0;
  return [(v >>> 24) & 255, (v >>> 16) & 255, (v >>> 8) & 255, v & 255];
}
const color = (s: unknown, fallback: string) => {
  const c = parseArgb(s);
  return c ? "#" + c.map((x) => x.toString(16).padStart(2, "0").toUpperCase()).join("") : fallback;
};
const name = (s: unknown, fallback: string) => {
  let t = typeof s === "string" ? s.replace(/[\p{C}]/gu, "").trim() : "";
  if (t.length > MAX_NAME) t = t.slice(0, MAX_NAME).trim();
  return t.length === 0 ? fallback : t;
};
const defaultName = (k: Kind) => (k === "Lines" ? "Lines" : k === "Dot" ? "Centre dot" : k === "Circle" ? "Circle" : "Image");

/** Untrusted JSON → a complete, clamped profile (same ranges and defaults as the app). Image layers keep only "img:&lt;id&gt;". */
export function sanitize(raw: unknown): Profile | null {
  if (!isObj(raw)) return null;
  const layersRaw = Array.isArray(raw.layers) ? raw.layers.filter(isObj).slice(0, MAX_LAYERS) : [];
  const ids = new Set<string>();
  const layers: Layer[] = layersRaw.map((l) => {
    const kind: Kind = KINDS.includes(l.kind as Kind) ? (l.kind as Kind) : "Lines";
    let id = typeof l.id === "string" && l.id.trim() ? l.id.trim().slice(0, 40) : newId();
    if (ids.has(id)) id = newId();
    ids.add(id);
    const img = typeof l.imageFile === "string" && /^img:[0-9a-f]{32}$/.test(l.imageFile) ? l.imageFile : null;
    return {
      id, kind, name: name(l.name, defaultName(kind)), visible: bool(l.visible, true),
      color: color(l.color, "#FF2BE38B"), opacity: clamp(l.opacity, 0, 1, 1), thickness: clamp(l.thickness, 0.5, 50, 2),
      length: clamp(l.length, 0, 300, 10), gap: clamp(l.gap, -50, 300, 4),
      top: bool(l.top, true), bottom: bool(l.bottom, true), left: bool(l.left, true), right: bool(l.right, true), roundEnds: bool(l.roundEnds, false),
      size: clamp(l.size, 0.5, 1000, 4), dotShape: l.dotShape === "Round" ? "Round" : "Square",
      outline: bool(l.outline, true), outlineColor: color(l.outlineColor, "#D9000000"), outlineThickness: clamp(l.outlineThickness, 0, 10, 1),
      offsetX: clamp(l.offsetX, -2000, 2000, 0), offsetY: clamp(l.offsetY, -2000, 2000, 0), rotation: clamp(l.rotation, -360, 360, 0),
      reactsToFiring: bool(l.reactsToFiring, true), imageFile: img, imageAspect: clamp(l.imageAspect, 0.01, 100, 1),
    };
  });
  const f = isObj(raw.firing) ? raw.firing : {};
  const trig = ["LeftMouse", "RightMouse", "EitherMouse"].includes(f.trigger as string) ? (f.trigger as Firing["trigger"]) : "LeftMouse";
  const now = new Date().toISOString();
  return {
    id: newId(), name: name(raw.name, "Crosshair"), layers, pixelPerfect: bool(raw.pixelPerfect, true), created: now, modified: now,
    firing: {
      enabled: bool(f.enabled, false), trigger: trig, spread: clamp(f.spread, 0, 100, 6), riseMs: clamp(f.riseMs, 1, 5000, 120),
      recoverMs: clamp(f.recoverMs, 1, 5000, 220), firingOpacity: clamp(f.firingOpacity, 0, 1, 1), flash: bool(f.flash, false),
      flashColor: color(f.flashColor, "#FFFF3B3B"), hideWhileAiming: bool(f.hideWhileAiming, false),
    },
  };
}

// ---- geometry (CrosshairGeometry.Build at rest) -------------------------------------------------------------------

interface Shape { kind: "rect" | "ellipse" | "ring"; x: number; y: number; w: number; h: number; c: [number, number, number, number]; opacity: number; stroke: number; radius: number }
interface Drawing { rotation: number; px: number; py: number; shapes: Shape[] }
const CENTRE = 0.5;

function snapAxis(a: number, size: number): [number, number] {
  const b = a + size;
  if (a < CENTRE && b > CENTRE) {
    const n = Math.max(1, Math.round(size));
    return [Math.floor(CENTRE - n / 2 + 0.5), n];
  }
  const side = a >= CENTRE ? 1 : -1;
  const E = (e: number) => { const d = e - CENTRE; return CENTRE + (Math.abs(d) < 1e-9 ? side : Math.sign(d)) * (Math.floor(Math.abs(d) + 1e-9) + 0.5); };
  let s0 = E(a), s1 = E(b);
  if (s1 - s0 < 1) { if (b <= CENTRE) s0 = s1 - 1; else s1 = s0 + 1; }
  return [s0, s1 - s0];
}

export function build(p: Profile, scale = 1): Drawing[] {
  const out: Drawing[] = [];
  for (const l of p.layers) {
    if (!l.visible || l.kind === "Image") continue; // image layers aren't drawn into the generated preview
    const c = parseArgb(l.color)!, oc = parseArgb(l.outlineColor)!;
    const s = (v: number) => v * scale;
    const cx = CENTRE + s(l.offsetX), cy = CENTRE + s(l.offsetY);
    const o = l.outline ? Math.max(0, s(l.outlineThickness)) : 0;
    const snap = p.pixelPerfect && Math.abs(l.rotation % 360) < 0.001;
    const shapes: Shape[] = [];
    const rect = (x: number, y: number, w: number, h: number, radius: number) => {
      if (w <= 0 || h <= 0) return;
      if (snap) { [x, w] = snapAxis(x, w); [y, h] = snapAxis(y, h); }
      if (o > 0) shapes.push({ kind: "rect", x: x - o, y: y - o, w: w + 2 * o, h: h + 2 * o, c: oc, opacity: l.opacity, stroke: 0, radius: radius > 0 ? radius + o : 0 });
      shapes.push({ kind: "rect", x, y, w, h, c, opacity: l.opacity, stroke: 0, radius });
    };
    if (l.kind === "Lines") {
      const t = Math.max(0.5, s(l.thickness)), len = Math.max(0, s(l.length)), g = s(l.gap), r = l.roundEnds ? t / 2 : 0;
      if (l.left) rect(cx - g - len, cy - t / 2, len, t, r);
      if (l.right) rect(cx + g, cy - t / 2, len, t, r);
      if (l.top) rect(cx - t / 2, cy - g - len, t, len, r);
      if (l.bottom) rect(cx - t / 2, cy + g, t, len, r);
    } else if (l.kind === "Dot") {
      const d = Math.max(0.5, s(l.size));
      if (l.dotShape === "Round") {
        if (o > 0) shapes.push({ kind: "ellipse", x: cx - d / 2 - o, y: cy - d / 2 - o, w: d + 2 * o, h: d + 2 * o, c: oc, opacity: l.opacity, stroke: 0, radius: 0 });
        shapes.push({ kind: "ellipse", x: cx - d / 2, y: cy - d / 2, w: d, h: d, c, opacity: l.opacity, stroke: 0, radius: 0 });
      } else rect(cx - d / 2, cy - d / 2, d, d, 0);
    } else {
      const r = Math.max(0.5, s(l.size)), t = Math.max(0.5, s(l.thickness));
      if (o > 0) shapes.push({ kind: "ring", x: cx - r, y: cy - r, w: 2 * r, h: 2 * r, c: oc, opacity: l.opacity, stroke: t + 2 * o, radius: 0 });
      shapes.push({ kind: "ring", x: cx - r, y: cy - r, w: 2 * r, h: 2 * r, c, opacity: l.opacity, stroke: t, radius: 0 });
    }
    if (shapes.length > 0) out.push({ rotation: l.rotation, px: cx, py: cy, shapes });
  }
  return out;
}

function extent(drawings: Drawing[]): number {
  let max = 4;
  for (const d of drawings) {
    const pivot = Math.hypot(d.px - CENTRE, d.py - CENTRE);
    for (const sh of d.shapes) {
      const h = sh.stroke / 2;
      for (const [x, y] of [[sh.x - h, sh.y - h], [sh.x + sh.w + h, sh.y - h], [sh.x - h, sh.y + sh.h + h], [sh.x + sh.w + h, sh.y + sh.h + h]])
        max = Math.max(max, pivot + Math.hypot(x - d.px, y - d.py));
    }
  }
  return max;
}

// ---- preview ------------------------------------------------------------------------------------------------------

export const PREVIEW_SIZE = 256;
const BG: [number, number, number] = [0x0b, 0x0e, 0x14];

function coverage(sh: Shape, x: number, y: number): number {
  const a = (sh.c[0] / 255) * Math.min(1, Math.max(0, sh.opacity));
  if (sh.kind === "rect") {
    if (x < sh.x || x >= sh.x + sh.w || y < sh.y || y >= sh.y + sh.h) return 0;
    if (sh.radius > 0) {
      const r = Math.min(sh.radius, Math.min(sh.w, sh.h) / 2);
      const qx = Math.max(sh.x + r - x, x - (sh.x + sh.w - r), 0), qy = Math.max(sh.y + r - y, y - (sh.y + sh.h - r), 0);
      if (qx * qx + qy * qy > r * r) return 0;
    }
    return a;
  }
  if (sh.kind === "ellipse") {
    const rx = sh.w / 2, ry = sh.h / 2, dx = (x - sh.x - rx) / rx, dy = (y - sh.y - ry) / ry;
    return dx * dx + dy * dy <= 1 ? a : 0;
  }
  const r = sh.w / 2;
  return Math.abs(Math.hypot(x - sh.x - r, y - sh.y - r) - r) <= sh.stroke / 2 ? a : 0;
}

/**
 * Draws the crosshair pixel-exact (small), then enlarges it without smoothing onto a 256×256 dark PNG. Each shape only
 * touches the samples inside its own bounding box, and the work is budgeted so a preview stays within the Workers free
 * plan's CPU limit. Returns null when a crosshair is too complex to draw within budget (the app still shows it live).
 */
export async function renderPreview(p: Profile): Promise<Uint8Array | null> {
  let drawings = build(p, 1);
  const ext = extent(drawings);
  let n = Math.ceil(ext) * 2 + 7;
  if (n > 72) { drawings = build(p, (72 - 7) / 2 / Math.ceil(ext)); n = 72; } // big crosshairs are drawn scaled down
  const c = Math.floor(n / 2);

  // Bounding box of every shape in pixel coordinates (rotation included), to size the work.
  const boxes = drawings.map((d) => d.shapes.map((sh) => {
    const h = sh.stroke / 2;
    const pts = [[sh.x - h, sh.y - h], [sh.x + sh.w + h, sh.y - h], [sh.x - h, sh.y + sh.h + h], [sh.x + sh.w + h, sh.y + sh.h + h]];
    const rad = (d.rotation * Math.PI) / 180, cos = Math.cos(rad), sin = Math.sin(rad), rot = Math.abs(d.rotation % 360) > 0.001;
    const xs: number[] = [], ys: number[] = [];
    for (const [x, y] of pts) {
      const dx = x - d.px, dy = y - d.py;
      xs.push(rot ? d.px + dx * cos - dy * sin : x);
      ys.push(rot ? d.py + dx * sin + dy * cos : y);
    }
    return [Math.max(0, Math.floor(Math.min(...xs) + c)), Math.min(n, Math.ceil(Math.max(...xs) + c)), Math.max(0, Math.floor(Math.min(...ys) + c)), Math.min(n, Math.ceil(Math.max(...ys) + c))];
  }));
  const area = boxes.flat().reduce((a, [x0, x1, y0, y1]) => a + Math.max(0, x1 - x0) * Math.max(0, y1 - y0), 0);
  const BUDGET = 60000; // shape-samples
  const ss = area * 16 <= BUDGET ? 4 : area * 4 <= BUDGET ? 2 : area <= BUDGET ? 1 : 0;
  if (ss === 0) return null;

  const W = n * ss;
  const buf = new Float32Array(W * W * 3);
  for (let i = 0; i < W * W; i++) { buf[i * 3] = BG[0] / 255; buf[i * 3 + 1] = BG[1] / 255; buf[i * 3 + 2] = BG[2] / 255; }
  drawings.forEach((d, di) => {
    const rot = Math.abs(d.rotation % 360) > 0.001, ang = (-d.rotation * Math.PI) / 180, cos = Math.cos(ang), sin = Math.sin(ang);
    d.shapes.forEach((sh, si) => {
      const [x0, x1, y0, y1] = boxes[di][si];
      const r = sh.c[1] / 255, g = sh.c[2] / 255, b = sh.c[3] / 255;
      for (let sy = y0 * ss; sy < y1 * ss; sy++)
        for (let sx = x0 * ss; sx < x1 * ss; sx++) {
          let gx = (sx + 0.5) / ss - c, gy = (sy + 0.5) / ss - c;
          if (rot) { const dx = gx - d.px, dy = gy - d.py; gx = d.px + dx * cos - dy * sin; gy = d.py + dx * sin + dy * cos; }
          const a = coverage(sh, gx, gy);
          if (a <= 0) continue;
          const i = (sy * W + sx) * 3;
          buf[i] = r * a + buf[i] * (1 - a); buf[i + 1] = g * a + buf[i + 1] * (1 - a); buf[i + 2] = b * a + buf[i + 2] * (1 - a);
        }
    });
  });
  const small = new Uint8Array(n * n * 3), k = ss * ss;
  for (let y = 0; y < n; y++)
    for (let x = 0; x < n; x++) {
      let ar = 0, ag = 0, ab = 0;
      for (let sy = 0; sy < ss; sy++)
        for (let sx = 0; sx < ss; sx++) { const i = ((y * ss + sy) * W + x * ss + sx) * 3; ar += buf[i]; ag += buf[i + 1]; ab += buf[i + 2]; }
      const o = (y * n + x) * 3;
      small[o] = Math.round((ar / k) * 255); small[o + 1] = Math.round((ag / k) * 255); small[o + 2] = Math.round((ab / k) * 255);
    }
  const zoom = Math.max(1, Math.floor(PREVIEW_SIZE / n)), off = Math.floor((PREVIEW_SIZE - n * zoom) / 2);
  const big = new Uint8Array(PREVIEW_SIZE * PREVIEW_SIZE * 3);
  for (let i = 0; i < PREVIEW_SIZE * PREVIEW_SIZE; i++) big.set(BG, i * 3);
  for (let y = 0; y < n; y++) {
    const row = new Uint8Array(n * zoom * 3);
    for (let x = 0; x < n; x++) for (let z = 0; z < zoom; z++) row.set(small.subarray((y * n + x) * 3, (y * n + x) * 3 + 3), (x * zoom + z) * 3);
    for (let z = 0; z < zoom; z++) big.set(row, ((y * zoom + z + off) * PREVIEW_SIZE + off) * 3);
  }
  return encodePng(big, PREVIEW_SIZE, PREVIEW_SIZE);
}
