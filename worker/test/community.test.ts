import { createExecutionContext, createScheduledController, env, waitOnExecutionContext } from "cloudflare:test";
import { afterEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { setClockForTests } from "../src/env";
import { readHeader, sniff } from "../src/images";
import { bytes } from "./fixtures";
import { encodePng } from "./png";

type Env = typeof env & Record<string, unknown>;
let counter = 0;
const uniq = (s: string) => `${s}${++counter}${Math.random().toString(36).slice(2, 6)}`;

async function call(method: string, path: string, o: { token?: string; json?: unknown; body?: Uint8Array; type?: string; host?: string; env?: Record<string, unknown> } = {}) {
  const headers: Record<string, string> = {};
  if (o.token) headers.authorization = `Bearer ${o.token}`;
  let body: BodyInit | undefined;
  if (o.json !== undefined) { headers["content-type"] = "application/json"; body = JSON.stringify(o.json); }
  if (o.body) { headers["content-type"] = o.type ?? "image/png"; body = o.body; }
  const req = new Request(`http://${o.host ?? "localhost"}${path}`, { method, headers, body });
  const ctx = createExecutionContext();
  const resp = await worker.fetch(req, { ...(env as Env), ...(o.env ?? {}) } as never, ctx);
  await waitOnExecutionContext(ctx);
  return resp;
}
const j = async (r: Response) => (await r.json()) as Record<string, any>;

async function signIn(login = uniq("user"), accountAgeDays = 365, e?: Record<string, unknown>) {
  const r = await call("POST", "/api/community/auth/dev", { json: { login, accountAgeDays }, env: e });
  expect(r.status).toBe(200);
  const s = await j(r);
  return { token: s.token as string, me: s.me, login };
}

const upload = (token: string, data: Uint8Array, type = "image/png", e?: Record<string, unknown>) => call("POST", "/api/community/images", { token, body: data, type, env: e });
const quota = async (token: string, e?: Record<string, unknown>) => (await j(await call("GET", "/api/community/me", { token, env: e }))).quota;

function publishBody(extra: Record<string, unknown> = {}) {
  return {
    title: "My Fortnite settings", description: "Competitive settings I use.", game: "Fortnite", category: "Competitive", tags: ["stretched", "1440p"],
    content: { settings: { resolutionWidth: 1440, resolutionHeight: 1080, stretched: true, refreshHz: 240, sensitivity: 6.5, keybinds: [{ action: "Wall", key: "Q" }] } },
    imageIds: [], ...extra,
  };
}
const crosshair = (imageFile?: string) => ({
  name: "Pro cross",
  layers: [{ kind: "Lines", color: "#FFFF00FF", thickness: 2, length: 10, gap: 4, outline: false }, ...(imageFile ? [{ kind: "Image", imageFile, size: 40 }] : [])],
});
async function publish(token: string, extra: Record<string, unknown> = {}) {
  const r = await call("POST", "/api/community/posts", { token, json: publishBody(extra) });
  expect(r.status, await r.clone().text()).toBe(200);
  return j(r);
}
async function storedImages() { return (await env.DB.prepare("SELECT COUNT(*) AS n FROM media_blobs").first<{ n: number }>())!.n; } // images stored in D1

afterEach(() => { setClockForTests(null); vi.unstubAllGlobals(); });

describe("website and service", () => {
  it("the entry module exports only the Worker handler (Cloudflare refuses other exports)", async () => {
    const mod = await import("../src/index");
    expect(Object.keys(mod)).toEqual(["default"]);
  });

  it("still serves the existing website from docs/", async () => {
    const r = await call("GET", "/");
    expect(r.status).toBe(200);
    expect(await r.text()).toContain("Money Tweaks");
    expect((await call("GET", "/assets/logo-512.png")).status).toBe(200);
    expect((await call("GET", "/api/other")).status).toBe(404); // only /api/community/ is the Hub
  });

  it("answers on /api/community/ and /api/community/health, reporting each part honestly", async () => {
    expect((await j(await call("GET", "/api/community/"))).health).toBe("/api/community/health");
    const h = await call("GET", "/api/community/health");
    expect(h.status).toBe(200);
    const body = await j(h);
    expect(body.ok).toBe(true);
    expect(body.checks.imageStorage.detail).toContain("D1");
    expect(Object.keys(body.checks)).toEqual(["database", "imageStorage", "imageProcessing", "githubSignIn", "moderators"]);
    expect(JSON.stringify(body)).not.toContain("test-secret"); // never reveals secret values
    const broken = await call("GET", "/api/community/health", { env: { DB: undefined, GITHUB_CLIENT_SECRET: undefined, ADMIN_GITHUB_IDS: "" } });
    expect(broken.status).toBe(503);
    const b = await j(broken);
    expect(b.ok).toBe(false);
    expect(b.checks.database.detail).toContain("D1 binding DB is missing");
    expect(b.checks.imageStorage.ok).toBe(false);
    expect(b.checks.githubSignIn.detail).toContain("GITHUB_CLIENT_SECRET secret");
    expect(b.checks.moderators.ok).toBe(false);
  });

  it("health notices missing tables", async () => {
    const empty = {
      prepare: () => ({ first: async () => ({ n: 0 }) }),
    };
    const b = await j(await call("GET", "/api/community/health", { env: { DB: empty } }));
    expect(b.checks.database.detail).toContain("tables are missing");
  });

  it("health notices unrelated tables that happen to use the same names", async () => {
    const clash = {
      prepare: (sql: string) => ({ first: async () => ({ n: 5 }), sql }),
      batch: async () => { throw new Error("no such column: provider_id"); },
    };
    const b = await j(await call("GET", "/api/community/health", { env: { DB: clash } }));
    expect(b.checks.database.ok).toBe(false);
    expect(b.checks.database.detail).toContain("different columns");
  });

  it("reports configuration honestly", async () => {
    const c = await j(await call("GET", "/api/community/config"));
    expect(c.dailyUploadLimit).toBe(2);
    expect(c.notConfigured).toEqual([]);
    const missing = await j(await call("GET", "/api/community/config", { env: { DB: undefined, IMAGES: undefined, GITHUB_CLIENT_SECRET: undefined } }));
    expect(missing.notConfigured.join(" ")).toMatch(/D1 database binding DB.*Images binding IMAGES|GITHUB_CLIENT_SECRET/);
    const r = await call("GET", "/api/community/posts", { env: { DB: undefined } });
    expect(r.status).toBe(503);
    expect((await j(r)).error).toBe("not_configured");
  });
});

describe("uploads", () => {
  it("keeps PNG transparency and removes JPEG metadata", async () => {
    const { token } = await signIn();
    const r = await upload(token, bytes("pngAlpha"));
    expect(r.status, await r.clone().text()).toBe(200);
    const up = await j(r);
    expect(up.image.hasAlpha).toBe(true);
    expect([up.image.width, up.image.height]).toEqual([64, 48]);
    const png = new Uint8Array(await (await call("GET", up.image.url, { token })).arrayBuffer());
    expect(sniff(png)).toBe("image/png");
    expect(png[25]).toBe(6); // colour type RGBA: alpha kept
    expect(readHeader(png)!.width).toBe(64);
    expect(new TextDecoder().decode(png)).not.toMatch(/tEXt|eXIf|iTXt/);

    const j1 = await upload(token, bytes("jpegExif"), "image/jpeg");
    expect(j1.status).toBe(200);
    const jp = await j(j1);
    const jpg = new Uint8Array(await (await call("GET", jp.image.url, { token })).arrayBuffer());
    expect(sniff(jpg)).toBe("image/jpeg");
    expect(new TextDecoder("latin1").decode(jpg)).not.toContain("SecretCamera");
    expect(new TextDecoder("latin1").decode(jpg)).not.toContain("Exif");
    expect(jp.image.hasAlpha).toBe(false);
  });

  it("shrinks big, detailed screenshots so they fit in D1", async () => {
    const { token } = await signIn();
    const w = 2400, h = 1600, rgb = new Uint8Array(w * h * 3);
    for (let o = 0; o < rgb.length; o += 65536) crypto.getRandomValues(rgb.subarray(o, Math.min(rgb.length, o + 65536)));
    for (let i = 0; i < rgb.length; i++) rgb[i] = (rgb[i] & 3) * 85; // 2 bits of noise per channel: detailed, ~3 MB as PNG
    const big = await encodePng(rgb, w, h);
    expect(big.length).toBeGreaterThan(1_800_000);
    expect(big.length).toBeLessThan(5 * 1048576);
    const r = await upload(token, big);
    expect(r.status, await r.clone().text()).toBe(200);
    const up = await j(r);
    expect(Math.max(up.image.width, up.image.height)).toBeLessThanOrEqual(1920);
    const row = await env.DB.prepare("SELECT MAX(bytes) AS n FROM media_blobs").first<{ n: number }>();
    expect(row!.n).toBeLessThanOrEqual(1_800_000);
    expect((await call("GET", up.image.url, { token })).status).toBe(200);
  });

  it("converts WebP for every client", async () => {
    const { token } = await signIn();
    const r = await upload(token, bytes("webp"), "image/webp");
    expect(r.status, await r.clone().text()).toBe(200);
    const up = await j(r);
    const out = new Uint8Array(await (await call("GET", up.image.url, { token })).arrayBuffer());
    expect(sniff(out)).toBe("image/jpeg");
  });

  it.each([
    ["svg", "image/svg+xml", 415, () => new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>')],
    ["exe", "application/octet-stream", 415, () => new Uint8Array([0x4d, 0x5a, 0x90, 0, ...new Array(200).fill(0)])],
    ["gif", "image/gif", 415, () => bytes("gif")],
    ["html", "image/png", 415, () => new TextEncoder().encode("<html><body>hi</body></html>")],
    ["mismatch", "image/jpeg", 415, () => bytes("pngOpaque")],
    ["corrupt", "image/png", 422, () => bytes("corrupt")],
    ["tiny", "image/png", 422, () => bytes("tiny")],
    ["wide", "image/png", 422, () => bytes("wide")],
    ["animated", "image/png", 422, () => bytes("apng")],
    ["empty", "image/png", 400, () => new Uint8Array(0)],
  ])("rejects %s without using the quota", async (_n, type, status, data) => {
    const { token } = await signIn();
    const before = await storedImages();
    const r = await upload(token, data(), type);
    expect(r.status).toBe(status);
    expect((await quota(token)).remaining).toBe(2);
    expect(await storedImages()).toBe(before);
  });

  it("rejects files over the size limit", async () => {
    const e = { MAX_UPLOAD_BYTES: "2048" };
    const { token } = await signIn(undefined, 365, e);
    const big = new Uint8Array(4096);
    big.set(bytes("pngOpaque"));
    const r = await upload(token, big, "image/png", e);
    expect(r.status).toBe(413);
    expect((await quota(token, e)).remaining).toBe(2);
  });

  it("allows 2 uploads per UTC day, then refuses until midnight UTC", async () => {
    setClockForTests(new Date("2026-10-20T21:30:00Z"));
    const { token, login } = await signIn();
    const a = await j(await upload(token, bytes("pngAlpha")));
    expect(a.quota.remaining).toBe(1);
    expect(a.quota.resetsAtUtc).toBe("2026-10-21T00:00:00.000Z");
    expect((await upload(token, bytes("pngOpaque"))).status).toBe(200);
    const third = await upload(token, bytes("webp"), "image/webp");
    expect(third.status).toBe(429);
    const body = await j(third);
    expect(body.error).toBe("quota_exceeded");
    expect(body.quota.remaining).toBe(0);
    // Deleting doesn't refund; a new session doesn't reset it; another account has its own allowance.
    expect((await call("DELETE", `/api/community/images/${a.image.id}`, { token })).status).toBe(204);
    expect((await upload(token, bytes("pngAlpha"))).status).toBe(429);
    const again = await signIn(login);
    expect((await upload(again.token, bytes("pngAlpha"))).status).toBe(429);
    const other = await signIn();
    expect((await upload(other.token, bytes("pngAlpha"))).status).toBe(200);
    setClockForTests(new Date("2026-10-20T23:59:59Z"));
    expect((await upload(token, bytes("pngAlpha"))).status).toBe(429);
    setClockForTests(new Date("2026-10-21T00:00:01Z"));
    expect((await upload(token, bytes("pngAlpha"))).status).toBe(200);
  });

  it("parallel uploads can't exceed the limit", async () => {
    const { token, login } = await signIn();
    const second = await signIn(login); // a second session of the same account
    const before = await storedImages();
    const results = await Promise.all(Array.from({ length: 12 }, (_, i) => upload(i % 2 ? second.token : token, bytes("pngOpaque"))));
    const statuses = results.map((r) => r.status);
    expect(statuses.filter((s) => s === 200)).toHaveLength(2);
    expect(statuses.filter((s) => s === 429)).toHaveLength(10);
    expect(await quota(token)).toMatchObject({ used: 2, remaining: 0 });
    expect(await storedImages()).toBe(before + 4); // 2 images + 2 thumbnails
  });

  it("needs a real, signed-in, established account", async () => {
    expect((await upload("", bytes("pngOpaque"))).status).toBe(401);
    expect((await upload("not-a-real-session-token-1234567890", bytes("pngOpaque"))).status).toBe(401);
    const young = await signIn(undefined, 2);
    const r = await upload(young.token, bytes("pngOpaque"));
    expect(r.status).toBe(403);
    expect((await j(r)).message).toContain("younger than 7 days");
  });

  it("refuses uploads clearly when image processing or storage isn't configured", async () => {
    const { token } = await signIn();
    for (const e of [{ IMAGES: undefined }, { DB: undefined }]) {
      const r = await upload(token, bytes("pngOpaque"), "image/png", e);
      expect(r.status).toBe(503);
      expect((await j(r)).error).toBe("not_configured");
    }
    expect((await quota(token)).remaining).toBe(2);
  });

  it("reports the monthly image-processing limit clearly and doesn't use the quota", async () => {
    const { token } = await signIn();
    const limited = {
      info: env.IMAGES.info.bind(env.IMAGES),
      input: () => ({ output: async () => { throw new Error("IMAGES_TRANSFORM_ERROR 9422: transformation limit exceeded"); }, transform() { return this; } }),
    };
    const r = await upload(token, bytes("pngOpaque"), "image/png", { IMAGES: limited });
    expect(r.status).toBe(503);
    expect((await j(r)).error).toBe("processing_limit");
    expect((await quota(token)).remaining).toBe(2);
  });

  it("can use a KV namespace instead of D1 for images if one is bound", async () => {
    const e = { MEDIA_KV: (env as Env).MEDIA_KV_TEST };
    const { token } = await signIn(undefined, 365, e);
    const up = await j(await upload(token, bytes("pngAlpha"), "image/png", e));
    const r = await call("GET", up.image.url, { token, env: e });
    expect(r.status).toBe(200);
    expect(sniff(new Uint8Array(await r.arrayBuffer()))).toBe("image/png");
  });

  it("keeps pending images private until a moderator approves", async () => {
    const e = { IMAGE_REVIEW: "pre" };
    const owner = await signIn(undefined, 365, e), stranger = await signIn(undefined, 365, e), admin = await signIn(uniq("admin"), 365, e);
    const up = await j(await upload(owner.token, bytes("pngOpaque"), "image/png", e));
    expect(up.image.status).toBe("pending");
    const post = await j(await call("POST", "/api/community/posts", { token: owner.token, json: publishBody({ imageIds: [up.image.id] }), env: e }));
    expect((await call("GET", up.image.url, { token: owner.token, env: e })).status).toBe(200);
    expect((await call("GET", up.image.url, { token: admin.token, env: e })).status).toBe(200);
    expect((await call("GET", up.image.url, { token: stranger.token, env: e })).status).toBe(404);
    expect((await call("GET", up.image.url, { env: e })).status).toBe(404);
    const seen = await j(await call("GET", `/api/community/posts/${post.id}`, { env: e }));
    expect(seen.images.some((i: { id: string }) => i.id === up.image.id)).toBe(false);
    expect((await call("POST", `/api/community/admin/images/${up.image.id}/approve`, { token: owner.token, env: e })).status).toBe(403);
    const q = await j(await call("GET", "/api/community/admin/queue", { token: admin.token, env: e }));
    expect(q.pendingImages.some((i: { image: { id: string } }) => i.image.id === up.image.id)).toBe(true);
    expect((await call("POST", `/api/community/admin/images/${up.image.id}/approve`, { token: admin.token, env: e })).status).toBe(204);
    const pub = await call("GET", up.image.url, { env: e });
    expect(pub.status).toBe(200);
    expect(pub.headers.get("x-content-type-options")).toBe("nosniff");
    expect(pub.headers.get("cache-control")).toContain("public");
    const up2 = await j(await upload(owner.token, bytes("pngAlpha"), "image/png", e));
    const before = await storedImages();
    expect((await call("POST", `/api/community/admin/images/${up2.image.id}/reject`, { token: admin.token, env: e })).status).toBe(204);
    expect(await storedImages()).toBe(before - 2); // rejected files deleted at once
  });

  it("media URLs can't reach anything else", async () => {
    for (const p of ["/media/..%2F..%2Fx/full", "/media/0123/full", "/media/0123456789abcdef0123456789abcdef/raw"])
      expect((await call("GET", p)).status).toBe(404);
  });
});

describe("posts", () => {
  it("publish, edit and delete with author-only permissions; files cleaned up", async () => {
    const alice = await signIn(), bob = await signIn();
    const up = await j(await upload(alice.token, bytes("pngOpaque")));
    const before = await storedImages();
    const post = await publish(alice.token, { imageIds: [up.image.id], content: { settings: { fpsCap: 240 }, crosshair: crosshair() } });
    expect(post.sections).toEqual(expect.arrayContaining(["Game settings", "Crosshair"]));
    expect(post.canEdit).toBe(true);
    expect(post.images.map((i: { kind: string }) => i.kind)).toEqual(["upload"]); // no server-made preview image
    expect(await storedImages()).toBe(before);
    expect((await quota(alice.token)).remaining).toBe(1);

    const edit = publishBody({ title: "Bob was here" });
    expect((await call("PUT", `/api/community/posts/${post.id}`, { token: bob.token, json: edit })).status).toBe(403);
    expect((await call("DELETE", `/api/community/posts/${post.id}`, { token: bob.token })).status).toBe(403);
    expect((await call("DELETE", `/api/community/posts/${post.id}`)).status).toBe(401);
    const upd = await call("PUT", `/api/community/posts/${post.id}`, { token: alice.token, json: publishBody({ title: "Updated: Fortnite settings" }) });
    expect(upd.status).toBe(200);
    const u = await j(upd);
    expect(u.title).toBe("Updated: Fortnite settings");
    expect(u.images).toHaveLength(0); // image dropped and no crosshair any more: files deleted
    expect(await storedImages()).toBe(before - 2);
    expect((await call("DELETE", `/api/community/posts/${post.id}`, { token: alice.token })).status).toBe(204);
    expect((await call("GET", `/api/community/posts/${post.id}`)).status).toBe(404);
  });

  it("post lists carry the crosshair so the app can draw a sharp preview itself", async () => {
    const a = await signIn(uniq("xauthor"));
    const before = await storedImages();
    const post = await publish(a.token, { title: "Sharp crosshair test", content: { crosshair: crosshair() } });
    expect(await storedImages()).toBe(before); // nothing rendered or stored on the server
    const list = await j(await call("GET", `/api/community/posts?author=${a.login}`));
    const item = list.items.find((i: { id: string }) => i.id === post.id);
    expect(item.crosshair.layers[0].color).toBe("#FFFF00FF");
    expect(item.crosshair.layers[0].thickness).toBe(2);
    expect(item.thumbUrl).toBeNull();
    const plain = await publish(a.token, { title: "No crosshair here" });
    expect((await j(await call("GET", `/api/community/posts?author=${a.login}`))).items.find((i: { id: string }) => i.id === plain.id).crosshair).toBeNull();
  });

  it("removes old server-made preview images", async () => {
    const a = await signIn();
    const post = await publish(a.token, { title: "Old preview post", content: { crosshair: crosshair() } });
    await env.DB.prepare(`INSERT INTO images (id, owner_id, post_id, kind, status, width, height, has_alpha, mime, bytes, thumb_mime, thumb_bytes, storage_key, thumb_key, created_utc)
      VALUES ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', ?1, ?2, 'crosshair-preview', 'approved', 256, 256, 0, 'image/png', 3, 'image/png', 3, 'img/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png', 'thumb/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png', ?3)`)
      .bind(a.me.user.id, post.id, new Date().toISOString()).run();
    expect((await j(await call("GET", `/api/community/posts/${post.id}`))).images).toHaveLength(0); // hidden right away
    const ctx = createExecutionContext();
    await worker.scheduled!(createScheduledController(), env as never, ctx);
    await waitOnExecutionContext(ctx);
    const left = await env.DB.prepare("SELECT COUNT(*) AS n FROM images WHERE kind = 'crosshair-preview' AND deleted_utc IS NULL").first<{ n: number }>();
    expect(left!.n).toBe(0);
  });

  it("deleting a post deletes its image files but not the quota use", async () => {
    const a = await signIn();
    const before = await storedImages();
    const up = await j(await upload(a.token, bytes("pngOpaque")));
    const post = await publish(a.token, { imageIds: [up.image.id], content: { crosshair: crosshair() } });
    expect(await storedImages()).toBe(before + 2);
    expect((await call("DELETE", `/api/community/posts/${post.id}`, { token: a.token })).status).toBe(204);
    expect(await storedImages()).toBe(before);
    expect((await quota(a.token)).remaining).toBe(1);
  });

  it("rejects invalid posts and clamps crosshairs", async () => {
    const c = await signIn(), other = await signIn();
    const bad = async (body: unknown) => (await call("POST", "/api/community/posts", { token: c.token, json: body })).status;
    const ok = { settings: { fpsCap: 240 } };
    expect(await bad({ title: "hi", game: "Fortnite", category: "Other", content: ok })).toBe(400);
    expect(await bad({ title: "Valid title", game: "Nope", category: "Other", content: ok })).toBe(400);
    expect(await bad({ title: "Valid title", game: "Fortnite", category: "Other", content: {} })).toBe(400);
    expect(await bad({ title: "Valid title", game: "Fortnite", category: "Other", content: { preset: { name: "x", tweakIds: ["gen.ok", "rm -rf /;"] } } })).toBe(400);
    expect(await bad({ title: "Valid title", game: "Fortnite", category: "Other", content: { gamingProfile: { name: "x", tweakIds: ["gen.ok"], gameExes: ["C:\\evil\\game.exe"] } } })).toBe(400);
    expect(await bad({ title: "Valid title", game: "Fortnite", category: "Other", description: "http://a.com http://b.com http://c.com", content: ok })).toBe(400);
    expect(await bad({ title: "Valid title", game: "Fortnite", category: "Other", content: ok, imageIds: ["0123456789abcdef0123456789abcdef"] })).toBe(400);
    const theirs = await j(await upload(other.token, bytes("pngOpaque")));
    expect(await bad({ title: "Valid title", game: "Fortnite", category: "Other", content: ok, imageIds: [theirs.image.id] })).toBe(400);
    const raw = await call("POST", "/api/community/posts", { token: c.token, json: "{not json" as unknown });
    expect(raw.status).toBe(400);
    const post = await publish(c.token, {
      content: { crosshair: { name: "x", layers: [{ kind: "Lines", thickness: 1e9, gap: 5, color: "nope" }, { kind: "Image", imageFile: "0123456789012345678901234567890123456789.png" }] } },
    });
    expect(post.content.crosshair.layers[0].thickness).toBe(50);
    expect(post.content.crosshair.layers[0].color).toBe("#FF2BE38B");
    expect(post.content.crosshair.layers[1].imageFile).toBeNull();
  });

  it("likes, bookmarks, helpfulness, imports, search and sorting", async () => {
    const a = await signIn(uniq("author")), v1 = await signIn(), v2 = await signIn();
    const p1 = await publish(a.token, { title: "Geometry wave settings zq" + counter });
    const p2 = await publish(a.token, { title: "Fortnite stretched 1440 zq" + counter });
    expect((await j(await call("PUT", `/api/community/posts/${p2.id}/like`, { token: v1.token }))).liked).toBe(true);
    await call("PUT", `/api/community/posts/${p2.id}/like`, { token: v1.token });
    await call("PUT", `/api/community/posts/${p2.id}/like`, { token: v2.token });
    await call("PUT", `/api/community/posts/${p1.id}/bookmark`, { token: v1.token });
    await call("PUT", `/api/community/posts/${p2.id}/helpful`, { token: v1.token, json: { helpful: true } });
    await call("PUT", `/api/community/posts/${p2.id}/helpful`, { token: v2.token, json: { helpful: false } });
    await call("PUT", `/api/community/posts/${p2.id}/helpful`, { token: v2.token, json: { helpful: true } });
    expect((await call("PUT", `/api/community/posts/${p2.id}/helpful`, { token: a.token, json: { helpful: true } })).status).toBe(400);
    await call("POST", `/api/community/posts/${p1.id}/imported`, { token: v1.token });
    await call("POST", `/api/community/posts/${p1.id}/imported`, { token: v1.token });
    const d2 = await j(await call("GET", `/api/community/posts/${p2.id}`));
    expect([d2.likes, d2.helpfulYes, d2.helpfulNo]).toEqual([2, 2, 0]);
    expect((await j(await call("GET", `/api/community/posts/${p1.id}`))).imports).toBe(1);
    const mine = (path: string) => call("GET", `/api/community/posts?author=${a.login}&${path}`).then(j);
    expect((await mine("sort=top")).items[0].id).toBe(p2.id);
    expect((await mine("sort=imports")).items[0].id).toBe(p1.id);
    const found = await j(await call("GET", "/api/community/posts?q=geometr"));
    expect(found.items.map((i: { id: string }) => i.id)).toContain(p1.id);
    expect(found.items.map((i: { id: string }) => i.id)).not.toContain(p2.id);
    expect((await call("GET", "/api/community/posts?q=%22)%20OR%201%3D1%20--")).status).toBe(200); // query syntax is neutralized
    expect((await j(await call("GET", "/api/community/me/bookmarks", { token: v1.token }))).items.map((i: { id: string }) => i.id)).toEqual([p1.id]);
    const author = await j(await call("GET", `/api/community/users/${a.login}`));
    expect([author.postCount, author.likesReceived]).toEqual([2, 2]);
  });

  it("reports hide a post; moderators restore, remove and suspend", async () => {
    const a = await signIn(), admin = await signIn(uniq("admin"));
    const post = await publish(a.token);
    for (let i = 0; i < 3; i++) {
      const r = await signIn();
      expect((await call("POST", `/api/community/posts/${post.id}/report`, { token: r.token, json: { reason: "Spam or advertising", details: "ad" } })).status).toBe(204);
      if (i === 0) expect((await call("POST", `/api/community/posts/${post.id}/report`, { token: r.token, json: { reason: "Spam or advertising" } })).status).toBe(409);
    }
    expect((await call("GET", `/api/community/posts/${post.id}`)).status).toBe(404);
    expect((await j(await call("GET", `/api/community/posts/${post.id}`, { token: a.token }))).status).toBe("hidden");
    const q = await j(await call("GET", "/api/community/admin/queue", { token: admin.token }));
    expect(q.hiddenPosts.some((p: { id: string }) => p.id === post.id)).toBe(true);
    await call("POST", `/api/community/admin/posts/${post.id}/restore`, { token: admin.token });
    expect((await call("GET", `/api/community/posts/${post.id}`)).status).toBe(200);
    await call("POST", `/api/community/admin/posts/${post.id}/remove`, { token: admin.token, json: { note: "rules" } });
    expect((await call("GET", `/api/community/posts/${post.id}`)).status).toBe(404);
    expect((await call("PUT", `/api/community/posts/${post.id}`, { token: a.token, json: publishBody() })).status).toBe(403);
    const post2 = await publish(a.token, { title: "Another post here" });
    expect((await call("POST", `/api/community/admin/users/${a.me.user.id}`, { token: admin.token, json: { banned: true, reason: "spam" } })).status).toBe(204);
    expect((await call("GET", "/api/community/me", { token: a.token })).status).toBe(401);
    expect((await call("GET", `/api/community/posts/${post2.id}`)).status).toBe(404);
    expect((await call("POST", "/api/community/auth/dev", { json: { login: a.login } })).status).toBe(403);
    expect((await call("GET", "/api/community/admin/queue", { token: (await signIn()).token })).status).toBe(403);
  });

  it("limits posts per day and refuses duplicates", async () => {
    const e = { POSTS_PER_DAY: "2" };
    const c = await signIn(undefined, 365, e);
    const post = (title: string) => call("POST", "/api/community/posts", { token: c.token, json: publishBody({ title }), env: e });
    expect((await post("First real post")).status).toBe(200);
    expect((await post("First real post")).status).toBe(409);
    expect((await post("Second real post")).status).toBe(200);
    expect((await post("Third post today")).status).toBe(429);
  });
});

describe("sign-in", () => {
  it("GitHub sign-in checks the token with GitHub and never stores it", async () => {
    const calls: string[] = [];
    vi.stubGlobal("fetch", async (input: RequestInfo, init?: RequestInit) => {
      const url = new URL(typeof input === "string" ? input : input.url);
      calls.push(`${init?.method ?? "GET"} ${url.pathname}`);
      const token = init?.body ? JSON.parse(init.body as string).access_token : (init?.headers as Record<string, string>).authorization.slice(7);
      if (token !== "gho_good_token") return new Response("{}", { status: 404 });
      return new Response(url.pathname === "/user"
        ? JSON.stringify({ id: 4242, login: "octo", avatar_url: "https://avatars.githubusercontent.com/u/4242", created_at: "2015-01-01T00:00:00Z" })
        : JSON.stringify({ id: 1 }), { status: 200 });
    });
    const r = await call("POST", "/api/community/auth/github", { json: { accessToken: "gho_good_token" } });
    expect(r.status).toBe(200);
    const s = await j(r);
    expect(s.me.user.login).toBe("octo");
    expect(s.me.isAdmin).toBe(true);
    expect(calls).toContain("POST /applications/Iv1.testclient/token");
    const stored = await env.DB.prepare("SELECT COUNT(*) AS n FROM sessions WHERE token_hash LIKE '%gho_good%'").first<{ n: number }>();
    expect(stored!.n).toBe(0);
    expect((await call("POST", "/api/community/auth/github", { json: { accessToken: "gho_bad_token1" } })).status).toBe(401);
    expect((await call("POST", "/api/community/auth/github", { json: { accessToken: "gho_good_token" }, env: { GITHUB_CLIENT_SECRET: undefined } })).status).toBe(503);
  });

  it("test sign-in only works locally and only when enabled", async () => {
    expect((await call("POST", "/api/community/auth/dev", { json: { login: "x" }, host: "money-tweaks.example.workers.dev" })).status).toBe(404);
    expect((await call("POST", "/api/community/auth/dev", { json: { login: "x" }, env: { DEV_SIGN_IN: undefined } })).status).toBe(404);
  });
});

describe("cleanup", () => {
  it("deletes unattached uploads after a day (still counted)", async () => {
    setClockForTests(new Date("2026-10-20T10:00:00Z"));
    const c = await signIn();
    const up = await j(await upload(c.token, bytes("pngOpaque")));
    const before = await storedImages();
    setClockForTests(new Date("2026-10-21T11:00:00Z"));
    const ctx = createExecutionContext();
    await worker.scheduled!(createScheduledController(), env as never, ctx);
    await waitOnExecutionContext(ctx);
    expect(await storedImages()).toBe(before - 2);
    expect((await call("GET", up.image.url, { token: c.token })).status).toBe(404);
  });
});
