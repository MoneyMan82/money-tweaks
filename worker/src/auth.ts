// Identity: GitHub accounts. The app signs in with GitHub's device flow and sends the Worker the GitHub token once;
// the Worker checks that the token belongs to Money Tweaks' own OAuth App (needs the client secret, a Worker secret),
// reads the account from GitHub, then issues its own random session token. Only a SHA-256 hash of the session token is
// stored, and the GitHub token is never stored. Quotas and permissions are tied to the GitHub account id.

import { ApiError, type Env, type Limits, iso, newId, now, sha256Hex, utcDay } from "./env";

export interface User {
  id: string; provider: string; providerId: string; login: string; avatarUrl: string | null;
  isAdmin: boolean; banned: boolean; trusted: boolean; providerCreated: Date | null; created: Date;
}

const GITHUB = "https://api.github.com";
const UA = "MoneyTweaks-Community/1.0 (+https://github.com/MoneyMan82/money-tweaks)";

function rowToUser(env: Env, r: Record<string, unknown>): User {
  const provider = r.provider as string, pid = r.provider_id as string;
  const admins = (env.ADMIN_GITHUB_IDS ?? "").split(",").map((s) => s.trim()).filter(Boolean);
  const isAdmin = (provider === "github" && admins.includes(pid)) || (provider === "dev" && env.DEV_SIGN_IN === "true" && pid.startsWith("admin"));
  return {
    id: r.id as string, provider, providerId: pid, login: r.login as string, avatarUrl: (r.avatar_url as string) ?? null, isAdmin,
    banned: r.is_banned === 1, trusted: r.is_trusted === 1,
    providerCreated: r.provider_created_utc ? new Date(r.provider_created_utc as string) : null, created: new Date(r.created_utc as string),
  };
}

async function upsert(env: Env, provider: string, pid: string, login: string, avatar: string | null, providerCreated: Date | null): Promise<User> {
  const db = env.DB!;
  await db.prepare(
    `INSERT INTO users (id, provider, provider_id, login, avatar_url, provider_created_utc, created_utc) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
     ON CONFLICT (provider, provider_id) DO UPDATE SET login = excluded.login, avatar_url = excluded.avatar_url,
       provider_created_utc = COALESCE(excluded.provider_created_utc, users.provider_created_utc)`,
  ).bind(newId(), provider, pid, login, avatar, providerCreated ? iso(providerCreated) : null, iso(now())).run();
  const r = await db.prepare("SELECT * FROM users WHERE provider = ?1 AND provider_id = ?2").bind(provider, pid).first();
  return rowToUser(env, r!);
}

export async function findUser(env: Env, id: string): Promise<User | null> {
  const r = await env.DB!.prepare("SELECT * FROM users WHERE id = ?1").bind(id).first();
  return r ? rowToUser(env, r) : null;
}

export async function findByLogin(env: Env, login: string): Promise<User | null> {
  const r = await env.DB!.prepare("SELECT * FROM users WHERE login = ?1 COLLATE NOCASE").bind(login).first();
  return r ? rowToUser(env, r) : null;
}

export async function signInGitHub(env: Env, token: unknown): Promise<User> {
  if (typeof token !== "string" || token.length < 10 || token.length > 400) throw new ApiError(401, "auth_failed", "Missing GitHub token.");
  if (!env.GITHUB_CLIENT_ID || !env.GITHUB_CLIENT_SECRET)
    throw new ApiError(503, "not_configured", "GitHub sign-in isn't set up on this server yet (GITHUB_CLIENT_ID / GITHUB_CLIENT_SECRET).");
  // 1. The token must belong to Money Tweaks' OAuth App.
  const check = await fetch(`${GITHUB}/applications/${encodeURIComponent(env.GITHUB_CLIENT_ID)}/token`, {
    method: "POST",
    headers: {
      authorization: "Basic " + btoa(`${env.GITHUB_CLIENT_ID}:${env.GITHUB_CLIENT_SECRET}`),
      accept: "application/vnd.github+json", "content-type": "application/json", "user-agent": UA,
    },
    body: JSON.stringify({ access_token: token }),
  });
  if (!check.ok) throw new ApiError(401, "auth_failed", "GitHub didn't accept this sign-in. Try again.");
  // 2. Who it is.
  const resp = await fetch(`${GITHUB}/user`, { headers: { authorization: `Bearer ${token}`, accept: "application/vnd.github+json", "user-agent": UA } });
  if (!resp.ok) throw new ApiError(401, "auth_failed", "GitHub didn't accept this sign-in. Try again.");
  const u = (await resp.json()) as { id?: number; login?: string; avatar_url?: string; created_at?: string };
  if (typeof u.id !== "number" || typeof u.login !== "string") throw new ApiError(401, "auth_failed", "GitHub returned no account.");
  const avatar = typeof u.avatar_url === "string" && u.avatar_url.startsWith("https://avatars.githubusercontent.com/") ? u.avatar_url : null;
  return upsert(env, "github", String(u.id), u.login, avatar, u.created_at ? new Date(u.created_at) : null);
}

/** Local development only: needs DEV_SIGN_IN=true (worker/.dev.vars) and a request to localhost. */
export async function signInDev(env: Env, request: Request, loginRaw: unknown, ageDays: unknown): Promise<User> {
  const host = new URL(request.url).hostname;
  if (env.DEV_SIGN_IN !== "true" || !["localhost", "127.0.0.1"].includes(host)) throw new ApiError(404, "not_found", "Not found.");
  const login = (typeof loginRaw === "string" ? loginRaw : "").replace(/[^A-Za-z0-9-]/g, "").slice(0, 39);
  if (!login) throw new ApiError(400, "invalid", "Choose a user name.");
  const age = typeof ageDays === "number" ? ageDays : 365;
  return upsert(env, "dev", login.toLowerCase(), login, null, new Date(now().getTime() - age * 86400000));
}

const b64url = (b: Uint8Array) => btoa(String.fromCharCode(...b)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");

export async function createSession(env: Env, u: User, lim: Limits): Promise<{ token: string; expires: Date }> {
  const token = b64url(crypto.getRandomValues(new Uint8Array(32)));
  const expires = new Date(now().getTime() + lim.sessionDays * 86400000);
  await env.DB!.prepare("INSERT INTO sessions (token_hash, user_id, created_utc, expires_utc) VALUES (?1, ?2, ?3, ?4)")
    .bind(await sha256Hex(token), u.id, iso(now()), iso(expires)).run();
  return { token, expires };
}

function bearer(request: Request): string | null {
  const h = request.headers.get("authorization") ?? "";
  if (!h.startsWith("Bearer ")) return null;
  const t = h.slice(7).trim();
  return t.length >= 20 && t.length <= 100 ? t : null;
}

/** The signed-in user, or null. */
export async function authenticate(env: Env, request: Request): Promise<User | null> {
  const t = bearer(request);
  if (!t || !env.DB) return null;
  const r = await env.DB.prepare(
    "SELECT u.* FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token_hash = ?1 AND s.expires_utc > ?2",
  ).bind(await sha256Hex(t), iso(now())).first();
  return r ? rowToUser(env, r) : null;
}

export async function requireUser(env: Env, request: Request): Promise<User> {
  const u = await authenticate(env, request);
  if (!u) throw new ApiError(401, "sign_in", "Sign in to do that.");
  return u;
}

export async function signOut(env: Env, request: Request) {
  const t = bearer(request);
  if (t && env.DB) await env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?1").bind(await sha256Hex(t)).run();
}

export function cannotContribute(u: User, lim: Limits): string | null {
  if (u.banned) return "This account is suspended from the Community Hub.";
  if (u.providerCreated && now().getTime() - u.providerCreated.getTime() < lim.minAccountAgeDays * 86400000)
    return `GitHub accounts younger than ${lim.minAccountAgeDays} days can't post or upload yet (spam protection).`;
  return null;
}

// ---- daily upload quota -------------------------------------------------------------------------------------------
// One row per used (or in-flight) slot in upload_slots (user, UTC day, slot). The INSERT picks the first free slot
// number up to the limit inside one atomic D1 batch, and the primary key makes duplicates impossible, so concurrent
// requests can't exceed the limit. Reserved before processing, released on failure, so failed uploads don't count.

export interface Quota { limit: number; used: number; remaining: number; resetsAtUtc: string }
export interface Reservation { userId: string; day: string; slot: number }

export async function quotaInfo(env: Env, userId: string, lim: Limits): Promise<Quota> {
  const t = now(), day = utcDay(t);
  const stale = iso(new Date(t.getTime() - lim.reservationMinutes * 60000));
  const r = await env.DB!.prepare("SELECT COUNT(*) AS n FROM upload_slots WHERE user_id = ?1 AND day = ?2 AND (state = 'used' OR reserved_utc > ?3)")
    .bind(userId, day, stale).first<{ n: number }>();
  const used = Math.min(r?.n ?? 0, lim.dailyUploads);
  const reset = new Date(Date.UTC(t.getUTCFullYear(), t.getUTCMonth(), t.getUTCDate() + 1));
  return { limit: lim.dailyUploads, used, remaining: Math.max(0, lim.dailyUploads - (r?.n ?? 0)), resetsAtUtc: iso(reset) };
}

export async function reserveSlot(env: Env, userId: string, lim: Limits): Promise<Reservation | null> {
  if (lim.dailyUploads <= 0) return null;
  const t = now(), day = utcDay(t);
  const stale = iso(new Date(t.getTime() - lim.reservationMinutes * 60000));
  const db = env.DB!;
  const [, ins] = await db.batch([
    db.prepare("DELETE FROM upload_slots WHERE user_id = ?1 AND day = ?2 AND state = 'reserved' AND reserved_utc <= ?3").bind(userId, day, stale),
    db.prepare(
      `WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < ?3)
       INSERT INTO upload_slots (user_id, day, slot, state, reserved_utc)
       SELECT ?1, ?2, x, 'reserved', ?4 FROM n WHERE x NOT IN (SELECT slot FROM upload_slots WHERE user_id = ?1 AND day = ?2) ORDER BY x LIMIT 1
       RETURNING slot`,
    ).bind(userId, day, lim.dailyUploads, iso(t)),
  ]);
  const slot = (ins.results as { slot: number }[])[0]?.slot;
  return slot ? { userId, day, slot } : null;
}

export async function commitSlot(env: Env, r: Reservation, imageId: string) {
  await env.DB!.prepare("UPDATE upload_slots SET state = 'used', image_id = ?4 WHERE user_id = ?1 AND day = ?2 AND slot = ?3")
    .bind(r.userId, r.day, r.slot, imageId).run();
}

export async function releaseSlot(env: Env, r: Reservation) {
  await env.DB!.prepare("DELETE FROM upload_slots WHERE user_id = ?1 AND day = ?2 AND slot = ?3 AND state = 'reserved'")
    .bind(r.userId, r.day, r.slot).run();
}
