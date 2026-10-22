// Tweak Truth Index: anonymous, opt-in measured results shared from the app, and their robust aggregates.
//
// Privacy: a submission row has no user id, login, IP, timestamp or free text. Only whitelisted fields are stored (tweak id,
// test kind, game from a fixed list, cleaned CPU/GPU model names, RAM size bucket, Windows build, app version and the
// per-metric changes), plus the day it arrived. Sign-in is required only to rate-limit sharing: that counter
// (evidence_quota) holds user id, day and a count, and is never linked to a submission.
//
// Aggregation: groups (all, by GPU, CPU, Windows version, game) per test kind; medians and shares, and nothing is published
// for a group until it has `minSubmissions` submissions.

import type { User } from "./auth";
import { ApiError, type Env, now, utcDay } from "./env";
import { GAMES } from "./rules";

export const METRICS = ["fps", "low1", "ft99", "cpu", "gpu", "ram", "cputemp", "gputemp"] as const;
const KINDS = ["synthetic", "lab", "own"] as const;
const VERDICTS = ["better", "worse", "same"] as const;
const RAM_SIZES = [4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256];
export const DAILY_SHARES = 50;
export const MIN_SUBMISSIONS = 5;

export interface Submission {
  key: string; tweak: string; kind: string; game: string | null; cpu: string; gpu: string; ramGb: number; osBuild: number; app: string;
  metrics: { k: string; pct: number; verdict: string; nBefore: number; nAfter: number }[];
}

let schemaReady = false;

/** Creates the tables on first use (same SQL as migrations/0003_evidence.sql), so no manual migration is needed. */
export async function ensureSchema(env: Env) {
  if (schemaReady || !env.DB) return;
  await env.DB.batch([
    env.DB.prepare(`CREATE TABLE IF NOT EXISTS evidence (
      key TEXT PRIMARY KEY, tweak TEXT NOT NULL, kind TEXT NOT NULL, game TEXT, cpu TEXT NOT NULL, gpu TEXT NOT NULL,
      ram_gb INTEGER NOT NULL, os_build INTEGER NOT NULL, app TEXT NOT NULL, metrics TEXT NOT NULL, day TEXT NOT NULL)`),
    env.DB.prepare("CREATE INDEX IF NOT EXISTS evidence_tweak ON evidence (tweak)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS evidence_quota (user_id TEXT NOT NULL, day TEXT NOT NULL, n INTEGER NOT NULL, PRIMARY KEY (user_id, day))"),
  ]);
  schemaReady = true;
}

const isInt = (v: unknown, lo: number, hi: number): v is number => typeof v === "number" && Number.isInteger(v) && v >= lo && v <= hi;
const isStr = (v: unknown, re: RegExp): v is string => typeof v === "string" && re.test(v);

/** Strict validation; anything not listed here is dropped, never stored. Returns the problems (empty = valid). */
export function validate(b: unknown): { sub?: Submission; problems: string[] } {
  const p: string[] = [];
  const o = (b ?? {}) as Record<string, unknown>;
  if (o.v !== 1) p.push("Unsupported format version.");
  if (!isStr(o.key, /^[0-9a-f]{32}$/)) p.push("Invalid key.");
  if (!isStr(o.tweak, /^[a-z0-9][a-z0-9._-]{1,63}$/)) p.push("Invalid tweak id.");
  if (!KINDS.includes(o.kind as never)) p.push("Invalid test kind.");
  if (o.game !== null && o.game !== undefined && !GAMES.includes(o.game as string)) p.push("Unknown game.");
  if (!isStr(o.cpu, /^[A-Za-z0-9 .+-]{1,48}$/)) p.push("Invalid CPU model.");
  if (!isStr(o.gpu, /^[A-Za-z0-9 .+-]{1,48}$/)) p.push("Invalid GPU model.");
  if (!RAM_SIZES.includes(o.ramGb as number)) p.push("Invalid RAM size.");
  if (!isInt(o.osBuild, 10240, 99999)) p.push("Invalid Windows build.");
  if (!isStr(o.app, /^\d{1,3}\.\d{1,3}(\.\d{1,4})?$/)) p.push("Invalid app version.");
  const metrics: Submission["metrics"] = [];
  if (!Array.isArray(o.metrics) || o.metrics.length < 1 || o.metrics.length > METRICS.length) p.push("Metrics must be a list of 1 to 8 entries.");
  else {
    const seen = new Set<string>();
    for (const m of o.metrics as Record<string, unknown>[]) {
      const k = m?.k as string;
      if (!METRICS.includes(k as never) || seen.has(k)) { p.push("Invalid or repeated metric."); break; }
      seen.add(k);
      if (typeof m.pct !== "number" || !Number.isFinite(m.pct) || m.pct < -90 || m.pct > 300) { p.push(`Implausible change for ${k}.`); break; }
      if (!VERDICTS.includes(m.verdict as never)) { p.push(`Invalid verdict for ${k}.`); break; }
      if (!isInt(m.nBefore, 2, 50) || !isInt(m.nAfter, 2, 50)) { p.push(`Each metric needs 2 to 50 runs per side (${k}).`); break; }
      metrics.push({ k, pct: Math.round(m.pct * 100) / 100, verdict: m.verdict as string, nBefore: m.nBefore, nAfter: m.nAfter });
    }
  }
  if (p.length > 0) return { problems: p };
  return {
    problems: [],
    sub: {
      key: o.key as string, tweak: o.tweak as string, kind: o.kind as string, game: (o.game as string | null) ?? null, cpu: (o.cpu as string).trim(),
      gpu: (o.gpu as string).trim(), ramGb: o.ramGb as number, osBuild: o.osBuild as number, app: o.app as string, metrics,
    },
  };
}

export async function submit(env: Env, u: User, body: unknown): Promise<{ accepted: boolean; duplicate: boolean; remainingToday: number }> {
  if (u.banned) throw new ApiError(403, "banned", "This account is suspended from the Community Hub.");
  const { sub, problems } = validate(body);
  if (!sub) throw new ApiError(400, "invalid", "This result can't be shared.", { problems });
  await ensureSchema(env);
  const db = env.DB!;
  const day = utcDay(now());
  // Atomic daily allowance: the counter only grows while under the limit.
  const q = await db.prepare(
    `INSERT INTO evidence_quota (user_id, day, n) VALUES (?1, ?2, 1)
     ON CONFLICT (user_id, day) DO UPDATE SET n = n + 1 WHERE n < ?3 RETURNING n`,
  ).bind(u.id, day, DAILY_SHARES).first<{ n: number }>();
  if (!q) throw new ApiError(429, "limit", `You can share up to ${DAILY_SHARES} results a day. Try again tomorrow.`);
  const r = await db.prepare(
    `INSERT INTO evidence (key, tweak, kind, game, cpu, gpu, ram_gb, os_build, app, metrics, day)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11) ON CONFLICT (key) DO NOTHING`,
  ).bind(sub.key, sub.tweak, sub.kind, sub.game, sub.cpu, sub.gpu, sub.ramGb, sub.osBuild, sub.app, JSON.stringify(sub.metrics), day).run();
  const accepted = (r.meta.changes ?? 0) > 0;
  return { accepted, duplicate: !accepted, remainingToday: DAILY_SHARES - q.n };
}

const median = (xs: number[]) => {
  const s = [...xs].sort((a, b) => a - b);
  return s.length === 0 ? null : s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2;
};
const round = (v: number | null, d = 1) => (v === null ? null : Math.round(v * 10 ** d) / 10 ** d);

interface Row { kind: string; game: string | null; cpu: string; gpu: string; os_build: number; metrics: string }

/** Aggregates for one tweak: per test kind, overall and by GPU, CPU, Windows version and game. */
export async function summary(env: Env, tweak: string, minSubmissions = MIN_SUBMISSIONS) {
  if (!/^[a-z0-9][a-z0-9._-]{1,63}$/.test(tweak)) throw new ApiError(400, "invalid", "Invalid tweak id.");
  await ensureSchema(env);
  // The newest 1,000 results per tweak: plenty for medians, and parsing stays far below the free plan's 10 ms CPU limit.
  const rows = (await env.DB!.prepare("SELECT kind, game, cpu, gpu, os_build, metrics FROM evidence WHERE tweak = ?1 ORDER BY day DESC LIMIT 1000").bind(tweak).all<Row>()).results;
  const parsed = rows.map((r) => ({ ...r, m: JSON.parse(r.metrics) as Submission["metrics"] }));
  const dims: [string, (r: Row) => string | null][] = [
    ["all", () => "All PCs"],
    ["gpu", (r) => r.gpu],
    ["cpu", (r) => r.cpu],
    ["os", (r) => (r.os_build >= 22000 ? "Windows 11" : "Windows 10")],
    ["game", (r) => r.game],
  ];
  const groups: unknown[] = [];
  for (const kind of KINDS) {
    const ofKind = parsed.filter((r) => r.kind === kind);
    if (ofKind.length === 0) continue;
    for (const [dimension, key] of dims) {
      const by = new Map<string, typeof ofKind>();
      for (const r of ofKind) { const v = key(r); if (v) (by.get(v) ?? by.set(v, []).get(v)!).push(r); }
      const top = [...by.entries()].sort((a, b) => b[1].length - a[1].length).slice(0, dimension === "all" ? 1 : 8);
      for (const [value, list] of top) {
        const enough = list.length >= minSubmissions;
        const metrics = !enough ? [] : METRICS.map((k) => {
          const ms = list.flatMap((r) => r.m.filter((x) => x.k === k));
          if (ms.length < minSubmissions) return { k, n: ms.length, medianPct: null, betterShare: null, worseShare: null };
          return {
            k, n: ms.length, medianPct: round(median(ms.map((x) => x.pct))),
            betterShare: round(ms.filter((x) => x.verdict === "better").length / ms.length, 3),
            worseShare: round(ms.filter((x) => x.verdict === "worse").length / ms.length, 3),
          };
        }).filter((m) => m.n > 0);
        groups.push({ dimension, value, kind, submissions: list.length, metrics });
      }
    }
  }
  return { tweak, minSubmissions, groups };
}

/** Moderation: removes every shared result for a tweak (e.g. after the tweak changed meaning). */
export async function clearTweak(env: Env, tweak: string) {
  await ensureSchema(env);
  return (await env.DB!.prepare("DELETE FROM evidence WHERE tweak = ?1").bind(tweak).run()).meta.changes ?? 0;
}

/** Old rate-limit counters are useless after a day; drop them. */
export async function cleanupQuota(env: Env) {
  if (!env.DB) return 0;
  await ensureSchema(env);
  return (await env.DB.prepare("DELETE FROM evidence_quota WHERE day < ?1").bind(utcDay(new Date(now().getTime() - 2 * 86400000))).run()).meta.changes ?? 0;
}
