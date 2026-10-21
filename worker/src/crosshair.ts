// Crosshair rules. Port of src/MoneyTweaks.Core/Crosshair (CrosshairModel defaults, CrosshairSanitizer): a shared
// crosshair is clamped exactly like the app clamps imports. Previews are drawn by the app itself from these settings
// (the same vector renderer as the overlay), so the server stores no preview images and spends no CPU on them.

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
