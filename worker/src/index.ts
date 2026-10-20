// Money Tweaks Worker: the website (docs/, served by the assets binding exactly as before) plus the Community Hub API.
// Only /api/v1/*, /media/* and /healthz reach this code (see run_worker_first in wrangler.jsonc).

import { authenticate, createSession, quotaInfo, requireUser, signInDev, signInGitHub, signOut, type User } from "./auth";
import { ApiError, type Env, type Limits, json, limits, missing } from "./env";
import { CATEGORIES, GAMES, GUIDELINES, REPORT_REASONS } from "./rules";
import * as svc from "./service";

const SECURITY = {
  "x-content-type-options": "nosniff",
  "referrer-policy": "no-referrer",
  "x-frame-options": "DENY",
  "strict-transport-security": "max-age=31536000",
};

async function body(request: Request, max = 256 * 1024): Promise<unknown> {
  const len = Number(request.headers.get("content-length") ?? "0");
  if (len > max) throw new ApiError(413, "too_large", "The request is too large.");
  const text = await request.text();
  if (text.length > max) throw new ApiError(413, "too_large", "The request is too large.");
  try { return text.length === 0 ? {} : JSON.parse(text); }
  catch { throw new ApiError(400, "invalid_json", "The request isn't valid JSON."); }
}

async function readUpload(request: Request, lim: Limits): Promise<Uint8Array> {
  const len = Number(request.headers.get("content-length") ?? "0");
  const tooLarge = () => new ApiError(413, "too_large", `Images can be at most ${Math.floor(lim.maxUploadBytes / 1048576)} MB.`);
  if (len > lim.maxUploadBytes) throw tooLarge();
  if (!request.body) return new Uint8Array(0);
  const reader = request.body.getReader();
  const parts: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.length;
    if (total > lim.maxUploadBytes) { await reader.cancel(); throw tooLarge(); }
    parts.push(value);
  }
  const out = new Uint8Array(total);
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

async function me(env: Env, u: User, lim: Limits) {
  return {
    user: { id: u.id, login: u.login, avatarUrl: u.avatarUrl, joinedUtc: u.created.toISOString(), postCount: 0, likesReceived: 0 },
    isAdmin: u.isAdmin, banned: u.banned, quota: await quotaInfo(env, u.id, lim),
  };
}

async function session(env: Env, u: User, lim: Limits) {
  if (u.banned) throw new ApiError(403, "banned", "This account is suspended from the Community Hub.");
  const s = await createSession(env, u, lim);
  return { token: s.token, expiresUtc: s.expires.toISOString(), me: await me(env, u, lim) };
}

const ok = (v: unknown) => json(v);
const done = () => new Response(null, { status: 204 });

async function route(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const path = url.pathname, method = request.method;
  const lim = limits(env);

  if (path === "/healthz") return json({ ok: true, configured: missing(env) });

  // Config works even before D1 exists, so the app can explain what's missing.
  if (path === "/api/v1/config" && method === "GET") {
    const m = missing(env);
    return ok({
      gitHubClientId: env.GITHUB_CLIENT_ID || null, dailyUploadLimit: lim.dailyUploads, maxUploadBytes: lim.maxUploadBytes,
      maxImageDimension: lim.maxDimension, minImageDimension: lim.minDimension, acceptedTypes: ["image/png", "image/jpeg", "image/webp"],
      games: GAMES, categories: CATEGORIES, reportReasons: REPORT_REASONS, guidelines: GUIDELINES,
      devSignIn: env.DEV_SIGN_IN === "true" && ["localhost", "127.0.0.1"].includes(url.hostname),
      notConfigured: [...new Set([...m.data, ...m.uploads, ...m.signIn])],
    });
  }
  if (!env.DB) throw new ApiError(503, "not_configured", "The Community Hub isn't set up on this server yet: the D1 database (binding DB) is missing.");

  // /media/{id}/{variant}
  const mm = path.match(/^\/media\/([^/]+)\/([^/]+)$/);
  if (mm && method === "GET") return (await svc.media(env, await authenticate(env, request), mm[1], mm[2])) ?? new Response("Not found", { status: 404 });
  if (!path.startsWith("/api/v1/")) return new Response("Not found", { status: 404 });
  const p = path.slice("/api/v1".length);
  const seg = p.split("/").filter(Boolean);
  const user = () => requireUser(env, request);

  // ---- auth ----
  if (p === "/auth/github" && method === "POST") return ok(await session(env, await signInGitHub(env, ((await body(request)) as Record<string, unknown>).accessToken), lim));
  if (p === "/auth/dev" && method === "POST") {
    const b = (await body(request)) as Record<string, unknown>;
    return ok(await session(env, await signInDev(env, request, b.login, b.accountAgeDays), lim));
  }
  if (p === "/auth/signout" && method === "POST") { await signOut(env, request); return done(); }
  if (p === "/me" && method === "GET") return ok(await me(env, await user(), lim));
  if (p === "/me/bookmarks" && method === "GET") return ok(await svc.listPosts(env, await user(), { bookmarks: true, page: Number(url.searchParams.get("page") ?? 1) }));
  if (p === "/me/posts" && method === "GET") return ok(await svc.listPosts(env, await user(), { mine: true, page: Number(url.searchParams.get("page") ?? 1) }));

  // ---- images ----
  if (p === "/images" && method === "POST") {
    const u = await user();
    return ok(await svc.upload(env, lim, u, await readUpload(request, lim), request.headers.get("content-type")));
  }
  if (seg[0] === "images" && seg.length === 2 && method === "DELETE") { await svc.deleteImage(env, await user(), seg[1]); return done(); }

  // ---- posts ----
  if (p === "/posts" && method === "GET") {
    const q = url.searchParams;
    return ok(await svc.listPosts(env, await authenticate(env, request), {
      q: q.get("q"), game: q.get("game"), category: q.get("category"), tag: q.get("tag"), sort: q.get("sort"), page: Number(q.get("page") ?? 1), author: q.get("author"),
    }));
  }
  if (p === "/posts" && method === "POST") { const u = await user(); return ok(await svc.createPost(env, lim, u, await body(request))); }
  if (seg[0] === "posts" && seg.length === 2) {
    const id = seg[1];
    if (method === "GET") { const d = await svc.getPost(env, await authenticate(env, request), id); return d ? ok(d) : json({ error: "not_found", message: "Post not found." }, 404); }
    if (method === "PUT") { const u = await user(); return ok(await svc.updatePost(env, lim, u, id, await body(request))); }
    if (method === "DELETE") { await svc.deletePost(env, await user(), id); return done(); }
  }
  if (seg[0] === "posts" && seg.length === 3) {
    const id = seg[1], action = seg[2];
    if (action === "like" && (method === "PUT" || method === "DELETE")) return ok(await svc.setLike(env, await user(), id, method === "PUT"));
    if (action === "bookmark" && (method === "PUT" || method === "DELETE")) return ok(await svc.setBookmark(env, await user(), id, method === "PUT"));
    if (action === "helpful" && method === "PUT") {
      const u = await user();
      const h = ((await body(request)) as Record<string, unknown>).helpful;
      return ok(await svc.setHelpful(env, u, id, typeof h === "boolean" ? h : null));
    }
    if (action === "imported" && method === "POST") { await svc.recordImport(env, await user(), id); return done(); }
    if (action === "report" && method === "POST") { const u = await user(); await svc.report(env, lim, u, id, await body(request)); return done(); }
  }
  if (seg[0] === "users" && seg.length === 2 && method === "GET") {
    const a = await svc.author(env, decodeURIComponent(seg[1]));
    return a ? ok(a) : json({ error: "not_found", message: "User not found." }, 404);
  }

  // ---- moderation ----
  if (seg[0] === "admin") {
    const u = await user();
    if (p === "/admin/queue" && method === "GET") return ok(await svc.queue(env, u));
    if (seg[1] === "images" && seg.length === 4 && method === "POST" && (seg[3] === "approve" || seg[3] === "reject")) { await svc.reviewImage(env, u, seg[2], seg[3] === "approve"); return done(); }
    if (seg[1] === "posts" && seg.length === 4 && method === "POST" && seg[3] === "remove") { await svc.setPostStatus(env, u, seg[2], "removed", ((await body(request)) as Record<string, unknown>).note); return done(); }
    if (seg[1] === "posts" && seg.length === 4 && method === "POST" && seg[3] === "restore") { await svc.setPostStatus(env, u, seg[2], "visible", null); return done(); }
    if (seg[1] === "reports" && seg.length === 4 && method === "POST" && seg[3] === "dismiss") { await svc.dismissReport(env, u, seg[2]); return done(); }
    if (seg[1] === "users" && seg.length === 3 && method === "POST") { await svc.setUserFlags(env, u, seg[2], await body(request)); return done(); }
    if (!u.isAdmin) throw new ApiError(403, "forbidden", "Moderators only.");
  }
  return json({ error: "not_found", message: "Not found." }, 404);
}

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (!(path.startsWith("/api/") || path.startsWith("/media/") || path === "/healthz")) return env.ASSETS.fetch(request); // the website, unchanged
    let resp: Response;
    try { resp = await route(request, env); }
    catch (e) {
      if (e instanceof ApiError) resp = json({ error: e.code, message: e.message, ...(e.extra ?? {}) }, e.status);
      else { console.error("unhandled", e); resp = json({ error: "server_error", message: "Something went wrong on the server." }, 500); }
    }
    const headers = new Headers(resp.headers);
    for (const [k, v] of Object.entries(SECURITY)) if (!headers.has(k)) headers.set(k, v);
    if (!headers.has("content-security-policy")) headers.set("content-security-policy", "default-src 'none'; frame-ancestors 'none'");
    if (!headers.has("cache-control")) headers.set("cache-control", "no-store");
    void ctx;
    return new Response(resp.body, { status: resp.status, statusText: resp.statusText, headers });
  },

  async scheduled(_event: ScheduledController, env: Env, ctx: ExecutionContext) {
    ctx.waitUntil(svc.cleanup(env, limits(env)).then((n) => { if (n > 0) console.log(`cleanup removed ${n} image(s)`); }));
  },
} satisfies ExportedHandler<Env>;
