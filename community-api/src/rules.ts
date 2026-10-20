// Validation rules for posts. Port of src/MoneyTweaks.Community.Shared/Contracts.cs (PostRules, Taxonomy): the app
// checks the same rules for instant feedback; these server-side checks are the authoritative ones.

export const GAMES = [
  "Fortnite", "Geometry Dash", "VALORANT", "Counter-Strike 2", "Apex Legends", "Rocket League", "Roblox", "Minecraft",
  "Marvel Rivals", "Call of Duty", "Overwatch 2", "League of Legends", "Rainbow Six Siege", "PUBG", "GTA V", "Other",
];
export const CATEGORIES = ["Competitive", "Performance", "Low-end PC", "Visual quality", "Stretched resolution", "Crosshair", "Streaming", "Other"];
export const REPORT_REASONS = [
  "Spam or advertising", "Offensive or hateful", "Sexual or graphic content", "Harassment", "Dangerous or malicious settings", "Misleading", "Copyright", "Other",
];
export const GUIDELINES =
  "• Share settings you actually use. Don't copy other people's posts as your own.\n" +
  "• Keep it clean: no hate, harassment, sexual or graphic content, and no slurs, in text or images.\n" +
  "• Screenshots should show gameplay or settings. No personal information (yours or anyone else's).\n" +
  "• No cheats, exploits, anti-cheat bypasses, injectors or links to them. Don't post settings meant to harm a PC.\n" +
  "• No spam, advertising, referral links or repeated posts.\n" +
  "• FPS and benchmark numbers are your own measurements: they're shown as user-reported.\n" +
  "• Report anything that breaks these rules. Moderators can hide posts and images and suspend accounts.";

export const LIMITS = {
  titleMin: 6, titleMax: 80, descriptionMax: 4000, maxTags: 8, tagMax: 24, maxImages: 4, maxTweakIds: 400,
  maxKeybinds: 60, maxGraphics: 60, maxLinks: 2, shortText: 80, notesMax: 1000,
};

export const ID_PATTERN = /^[0-9a-f]{32}$/;
const TAG = /^[a-z0-9][a-z0-9 +\-]{0,23}$/;
const TWEAK_ID = /^[a-z0-9][a-z0-9._\-]{1,80}$/;
const EXE = /^[A-Za-z0-9 ._()\-]{1,80}\.exe$/i;
const LINK = /https?:\/\/|www\./gi;
// Control, format, private-use and unassigned characters except newline and tab.
const CONTROL = /[\p{Cc}\p{Cf}\p{Co}\p{Cn}]/gu;

export function clean(s: unknown, max: number, multiline = false): string {
  let t = typeof s === "string" ? s : "";
  t = t.replace(CONTROL, (c) => (c === "\n" || c === "\t" ? c : ""));
  if (!multiline) t = t.replace(/[\n\t]/g, " ");
  t = t.trim();
  return t.length > max ? t.slice(0, max).trim() : t;
}

const opt = (s: unknown, max: number) => { const t = clean(s, max); return t.length === 0 ? null : t; };
const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const int = (v: unknown): number | null => (typeof v === "number" && Number.isInteger(v) ? v : null);
const links = (s: string) => (s.match(LINK) ?? []).length;

export interface Keybind { action: string; key: string }
export interface SettingPair { name: string; value: string }
export interface GameSettings {
  resolutionWidth: number | null; resolutionHeight: number | null; stretched: boolean | null; refreshHz: number | null; fpsCap: number | null;
  sensitivity: number | null; adsSensitivity: number | null; mouseDpi: number | null; keybinds: Keybind[]; graphics: SettingPair[]; notes: string | null;
}
export interface TweakBundle {
  name: string; description: string; tweakIds: string[]; power: string | null; timerResolutionMs: number | null; raiseGamePriority: boolean; gameExes: string[];
}
export interface HardwareInfo { cpu: string | null; gpu: string | null; ramGb: number | null; monitor: string | null }
export interface BenchmarkReport { averageFps: number | null; onePercentLowFps: number | null; scenario: string | null }
export interface PostContent {
  settings: GameSettings | null; crosshair: Record<string, unknown> | null; gamingProfile: TweakBundle | null; preset: TweakBundle | null;
  hardware: HardwareInfo | null; benchmark: BenchmarkReport | null;
}
export interface PostInput { title: string; description: string; game: string; category: string; tags: string[]; content: PostContent; imageIds: string[] }

export function sections(c: PostContent): string[] {
  const s: string[] = [];
  if (c.settings) s.push("Game settings");
  if (c.crosshair) s.push("Crosshair");
  if (c.gamingProfile) s.push("Gaming profile");
  if (c.preset) s.push("Preset");
  if (c.benchmark) s.push("Benchmark (user-reported)");
  return s;
}

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const arr = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);

/** Normalizes untrusted JSON into a PostInput and lists problems (empty = valid). Unknown fields are dropped. */
export function validatePost(raw: unknown): { input: PostInput; errors: string[] } {
  const e: string[] = [];
  const r = isObj(raw) ? raw : {};
  const input: PostInput = {
    title: clean(r.title, LIMITS.titleMax + 1),
    description: clean(r.description, LIMITS.descriptionMax + 1, true),
    game: typeof r.game === "string" ? r.game : "",
    category: typeof r.category === "string" ? r.category : "",
    tags: [...new Set(arr(r.tags).map((t) => clean(t, LIMITS.tagMax + 1).toLowerCase()).filter((t) => t.length > 0))],
    imageIds: [...new Set(arr(r.imageIds).filter((x): x is string => typeof x === "string"))],
    content: { settings: null, crosshair: null, gamingProfile: null, preset: null, hardware: null, benchmark: null },
  };
  if (input.title.length < LIMITS.titleMin || input.title.length > LIMITS.titleMax) e.push(`Title must be ${LIMITS.titleMin}–${LIMITS.titleMax} characters.`);
  if (input.description.length > LIMITS.descriptionMax) e.push(`Description can be at most ${LIMITS.descriptionMax} characters.`);
  if (links(input.title) > 0) e.push("Links aren't allowed in the title.");
  if (links(input.description) > LIMITS.maxLinks) e.push(`At most ${LIMITS.maxLinks} links in the description.`);
  if (!GAMES.includes(input.game)) e.push("Choose a game from the list.");
  if (!CATEGORIES.includes(input.category)) e.push("Choose a category from the list.");
  if (input.tags.length > LIMITS.maxTags) e.push(`At most ${LIMITS.maxTags} tags.`);
  if (input.tags.some((t) => !TAG.test(t))) e.push("Tags can use letters, numbers, spaces, + and - (up to 24 characters).");
  if (input.imageIds.length > LIMITS.maxImages) e.push(`At most ${LIMITS.maxImages} images per post.`);
  if (input.imageIds.some((i) => !ID_PATTERN.test(i))) e.push("Unknown image.");

  const c = isObj(r.content) ? r.content : {};
  if (isObj(c.settings)) input.content.settings = settings(c.settings, e);
  if (c.crosshair !== undefined && c.crosshair !== null) {
    if (isObj(c.crosshair)) input.content.crosshair = c.crosshair;
    else e.push("The crosshair isn't valid.");
  }
  if (isObj(c.gamingProfile)) input.content.gamingProfile = bundle(c.gamingProfile, "Gaming profile", e, true);
  if (isObj(c.preset)) input.content.preset = bundle(c.preset, "Preset", e, false);
  if (isObj(c.hardware)) {
    const h = c.hardware;
    const hw: HardwareInfo = { cpu: opt(h.cpu, LIMITS.shortText), gpu: opt(h.gpu, LIMITS.shortText), ramGb: int(h.ramGb), monitor: opt(h.monitor, LIMITS.shortText) };
    if (hw.ramGb !== null && (hw.ramGb < 1 || hw.ramGb > 2048)) e.push("RAM must be 1–2048 GB.");
    if (hw.cpu ?? hw.gpu ?? hw.ramGb ?? hw.monitor) input.content.hardware = hw;
  }
  if (isObj(c.benchmark)) {
    const b = c.benchmark;
    const bm: BenchmarkReport = { averageFps: num(b.averageFps), onePercentLowFps: num(b.onePercentLowFps), scenario: opt(b.scenario, LIMITS.shortText) };
    if ([bm.averageFps, bm.onePercentLowFps].some((v) => v !== null && (v < 0 || v > 5000))) e.push("FPS values must be 0–5000.");
    if (bm.averageFps !== null || bm.onePercentLowFps !== null) input.content.benchmark = bm;
  }
  const x = input.content;
  if (!x.settings && !x.crosshair && !x.gamingProfile && !x.preset) e.push("Add at least one thing to share: game settings, a crosshair, a gaming profile or a preset.");
  return { input, errors: e };
}

function settings(s: Record<string, unknown>, e: string[]): GameSettings {
  const g: GameSettings = {
    resolutionWidth: int(s.resolutionWidth), resolutionHeight: int(s.resolutionHeight), stretched: s.stretched === true ? true : null,
    refreshHz: int(s.refreshHz), fpsCap: int(s.fpsCap), sensitivity: num(s.sensitivity), adsSensitivity: num(s.adsSensitivity), mouseDpi: int(s.mouseDpi),
    keybinds: arr(s.keybinds).filter(isObj).map((k) => ({ action: clean(k.action, 40), key: clean(k.key, 24) })).filter((k) => k.action && k.key),
    graphics: arr(s.graphics).filter(isObj).map((k) => ({ name: clean(k.name, 40), value: clean(k.value, 40) })).filter((k) => k.name),
    notes: opt(s.notes, LIMITS.notesMax),
  };
  const out = (v: number | null, lo: number, hi: number) => v !== null && (v < lo || v > hi);
  if (out(g.resolutionWidth, 320, 15360) || out(g.resolutionHeight, 200, 8640)) e.push("Resolution must be between 320×200 and 15360×8640.");
  if ((g.resolutionWidth === null) !== (g.resolutionHeight === null)) e.push("Enter both resolution width and height.");
  if (out(g.refreshHz, 24, 1000)) e.push("Refresh rate must be 24–1000 Hz.");
  if (out(g.fpsCap, 0, 5000)) e.push("FPS cap must be 0 (unlimited) to 5000.");
  if (out(g.sensitivity, 0, 100000) || out(g.adsSensitivity, 0, 100000)) e.push("Sensitivity must be 0–100000.");
  if (out(g.mouseDpi, 50, 100000)) e.push("Mouse DPI must be 50–100000.");
  if (g.keybinds.length > LIMITS.maxKeybinds) e.push(`At most ${LIMITS.maxKeybinds} keybinds.`);
  if (g.graphics.length > LIMITS.maxGraphics) e.push(`At most ${LIMITS.maxGraphics} graphics settings.`);
  return g;
}

function bundle(b: Record<string, unknown>, what: string, e: string[], profile: boolean): TweakBundle {
  const t: TweakBundle = {
    name: clean(b.name, 60), description: clean(b.description, 500, true),
    tweakIds: [...new Set(arr(b.tweakIds).map((x) => (typeof x === "string" ? x.trim() : "")))],
    power: profile && typeof b.power === "string" ? b.power : null,
    timerResolutionMs: profile ? num(b.timerResolutionMs) : null,
    raiseGamePriority: profile && b.raiseGamePriority === true,
    gameExes: profile ? [...new Set(arr(b.gameExes).map((x) => (typeof x === "string" ? x.trim() : "")).filter((x) => x.length > 0))] : [],
  };
  if (!t.name) e.push(`${what} needs a name.`);
  if (t.tweakIds.length > LIMITS.maxTweakIds) e.push(`${what}: too many tweaks.`);
  if (t.tweakIds.some((x) => !TWEAK_ID.test(x))) e.push(`${what}: contains an invalid tweak id.`);
  if (t.gameExes.length > 20 || t.gameExes.some((x) => !EXE.test(x))) e.push(`${what}: game executables must be plain .exe names.`);
  if (t.power !== null && !["Keep", "HighPerformance", "Balanced"].includes(t.power)) e.push(`${what}: unknown power plan.`);
  if (t.timerResolutionMs !== null && (t.timerResolutionMs < 0.5 || t.timerResolutionMs > 15.625)) e.push(`${what}: timer resolution must be 0.5–15.625 ms.`);
  const changes = t.tweakIds.length > 0 || (profile && ((t.power !== null && t.power !== "Keep") || t.timerResolutionMs !== null || t.raiseGamePriority));
  if (!changes) e.push(`${what} doesn't change anything.`);
  return t;
}
