// Posts, images, reactions and moderation. Every function receives the acting user and checks permissions itself.
// Port of src/MoneyTweaks.Community.Server/CommunityService.cs, with the same JSON shapes so the app works unchanged.

import { cannotContribute, commitSlot, findByLogin, quotaInfo, releaseSlot, reserveSlot, type User } from "./auth";
import { sanitize } from "./crosshair";
import { ApiError, type Env, type Limits, iso, newId, now, sha256Hex } from "./env";
import { processUpload } from "./images";
import { CATEGORIES, GAMES, ID_PATTERN, REPORT_REASONS, clean, sections, validatePost, type PostContent, type PostInput } from "./rules";
import { mediaStore, type MediaStore } from "./storage";

type Row = Record<string, unknown>;
const forbidden = (m: string) => new ApiError(403, "forbidden", m);
const notFound = (m = "Post not found.") => new ApiError(404, "not_found", m);
const suspended = () => new ApiError(403, "not_allowed", "This account is suspended from the Community Hub.");

function store(env: Env): MediaStore {
  const s = mediaStore(env);
  if (!s) throw new ApiError(503, "not_configured", "Image storage isn't available: the D1 database (binding DB) is missing.");
  return s;
}

// ---- images -------------------------------------------------------------------------------------------------------

const imageInfo = (r: Row) => ({
  id: r.id as string, width: r.width as number, height: r.height as number, kind: r.kind as string, status: r.status as string,
  hasAlpha: r.has_alpha === 1, url: `/api/community/media/${r.id}/full`, thumbUrl: `/api/community/media/${r.id}/thumb`,
});

export async function upload(env: Env, lim: Limits, u: User, data: Uint8Array, contentType: string | null) {
  const why = cannotContribute(u, lim);
  if (why) throw new ApiError(403, "not_allowed", why);
  const media = store(env);
  if (!env.IMAGES) throw new ApiError(503, "not_configured", "Image uploads aren't available: this server has no Cloudflare Images binding (IMAGES) to safely re-encode them.");
  const total = await env.DB!.prepare("SELECT COALESCE(SUM(bytes + thumb_bytes), 0) AS n FROM images WHERE deleted_utc IS NULL").first<{ n: number }>();
  if ((total?.n ?? 0) + data.length * 2 > lim.maxTotalStorageBytes)
    throw new ApiError(507, "storage_full", "Image uploads are paused because the Community Hub's storage is full. Try again later.");
  const slot = await reserveSlot(env, u.id, lim);
  if (!slot) throw new ApiError(429, "quota_exceeded", `You've used today's ${lim.dailyUploads} image uploads. The limit resets at 00:00 UTC.`, { quota: await quotaInfo(env, u.id, lim) });
  const keys: string[] = [];
  try {
    const p = await processUpload(env, lim, data, contentType);
    const id = newId(), ext = p.fullMime === "image/png" ? "png" : "jpg";
    const full = `img/${id}.${ext}`, thumb = `thumb/${id}.${ext}`;
    keys.push(full);
    await media.put(full, p.full, p.fullMime);
    keys.push(thumb);
    await media.put(thumb, p.thumb, p.thumbMime);
    const status = lim.imageReview === "post" || u.trusted || u.isAdmin ? "approved" : "pending";
    await env.DB!.prepare(
      `INSERT INTO images (id, owner_id, post_id, kind, status, width, height, has_alpha, mime, bytes, thumb_mime, thumb_bytes, storage_key, thumb_key, created_utc)
       VALUES (?1, ?2, NULL, 'upload', ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)`,
    ).bind(id, u.id, status, p.width, p.height, p.alpha ? 1 : 0, p.fullMime, p.full.length, p.thumbMime, p.thumb.length, full, thumb, iso(now())).run();
    await commitSlot(env, slot, id);
    const row = await env.DB!.prepare("SELECT * FROM images WHERE id = ?1").bind(id).first();
    return { image: imageInfo(row!), quota: await quotaInfo(env, u.id, lim) };
  } catch (e) {
    await releaseSlot(env, slot); // a failed upload never uses the allowance
    for (const k of keys) await media.delete(k).catch(() => {});
    if (e instanceof ApiError) { e.extra = { ...(e.extra ?? {}), quota: await quotaInfo(env, u.id, lim) }; throw e; }
    console.error("upload failed", e);
    throw new ApiError(500, "upload_failed", "The image couldn't be saved. It didn't count toward your daily limit.", { quota: await quotaInfo(env, u.id, lim) });
  }
}

/** Marks matching images deleted and removes their files. */
async function purgeImages(env: Env, where: string, ...args: unknown[]): Promise<number> {
  const rows = (await env.DB!.prepare(`SELECT id, storage_key, thumb_key FROM images WHERE deleted_utc IS NULL AND (${where})`).bind(...args).all()).results;
  if (rows.length === 0) return 0;
  const media = mediaStore(env);
  for (const r of rows) {
    await env.DB!.prepare("UPDATE images SET deleted_utc = ?1 WHERE id = ?2").bind(iso(now()), r.id).run();
    if (media) for (const k of [r.storage_key, r.thumb_key] as string[]) await media.delete(k).catch((e) => console.warn("delete failed; cleanup retries", k, e));
  }
  return rows.length;
}

export async function deleteImage(env: Env, u: User, id: string) {
  const r = ID_PATTERN.test(id) ? await env.DB!.prepare("SELECT owner_id FROM images WHERE id = ?1 AND deleted_utc IS NULL AND kind = 'upload'").bind(id).first() : null;
  if (!r || (r.owner_id !== u.id && !u.isAdmin)) throw notFound("Image not found.");
  await purgeImages(env, "id = ?1", id);
}

export async function media(env: Env, viewer: User | null, id: string, variant: string): Promise<Response | null> {
  if (!ID_PATTERN.test(id) || (variant !== "full" && variant !== "thumb")) return null;
  const r = await env.DB!.prepare(
    `SELECT i.storage_key, i.thumb_key, i.mime, i.thumb_mime, i.owner_id, i.status, p.status AS post_status
     FROM images i LEFT JOIN posts p ON p.id = i.post_id WHERE i.id = ?1 AND i.deleted_utc IS NULL`,
  ).bind(id).first();
  if (!r) return null;
  const publicOk = r.status === "approved" && r.post_status === "visible";
  const privileged = viewer !== null && (viewer.id === r.owner_id || viewer.isAdmin);
  if (!publicOk && !privileged) return null;
  const s = mediaStore(env);
  const o = s ? await s.get((variant === "full" ? r.storage_key : r.thumb_key) as string) : null;
  if (!o) return null;
  return new Response(o.body, {
    headers: {
      "content-type": (variant === "full" ? r.mime : r.thumb_mime) as string,
      "cache-control": publicOk ? "public, max-age=3600" : "private, no-store",
      "content-disposition": "inline",
      "x-content-type-options": "nosniff",
      "content-security-policy": "default-src 'none'; frame-ancestors 'none'",
    },
  });
}

// ---- posts ----------------------------------------------------------------------------------------------------------

async function validate(env: Env, u: User, raw: unknown, postId: string | null): Promise<{ input: PostInput; content: string; hash: string }> {
  const { input, errors } = validatePost(raw);
  if (input.content.crosshair) {
    const p = sanitize(input.content.crosshair);
    if (!p) errors.push("The crosshair isn't valid.");
    else {
      for (const l of p.layers) if (l.kind === "Image" && (!l.imageFile || !input.imageIds.includes(l.imageFile.slice(4)))) l.imageFile = null;
      input.content.crosshair = p as unknown as Record<string, unknown>;
    }
  }
  for (const id of input.imageIds) {
    const r = await env.DB!.prepare("SELECT owner_id, post_id, kind FROM images WHERE id = ?1 AND deleted_utc IS NULL").bind(id).first();
    if (!r || r.owner_id !== u.id || r.kind !== "upload" || (r.post_id !== null && r.post_id !== postId)) {
      errors.push("One of the images can't be used (it isn't your upload, was deleted, or belongs to another post).");
      break;
    }
  }
  if (errors.length > 0) throw new ApiError(400, "invalid", [...new Set(errors)].join(" "), { problems: [...new Set(errors)] });
  const content = JSON.stringify(input.content);
  return { input, content, hash: await sha256Hex(input.title.toLowerCase() + "\n" + content) };
}

async function attachImages(env: Env, u: User, postId: string, ids: string[]) {
  const st = ids.map((id, i) => env.DB!.prepare("UPDATE images SET post_id = ?1, position = ?2 WHERE id = ?3 AND owner_id = ?4 AND kind = 'upload'").bind(postId, i, id, u.id));
  if (st.length > 0) await env.DB!.batch(st);
}

export async function createPost(env: Env, lim: Limits, u: User, raw: unknown) {
  const why = cannotContribute(u, lim);
  if (why) throw new ApiError(403, "not_allowed", why);
  const { input, content, hash } = await validate(env, u, raw, null);
  const db = env.DB!, t = now(), since = iso(new Date(t.getTime() - 86400000));
  const recent = await db.prepare("SELECT COUNT(*) AS n FROM activity WHERE user_id = ?1 AND kind = 'post' AND created_utc > ?2").bind(u.id, since).first<{ n: number }>();
  if ((recent?.n ?? 0) >= lim.postsPerDay) throw new ApiError(429, "post_limit", `You can publish up to ${lim.postsPerDay} posts per day.`);
  if (await db.prepare("SELECT 1 FROM posts WHERE author_id = ?1 AND content_hash = ?2 AND created_utc > ?3").bind(u.id, hash, since).first())
    throw new ApiError(409, "duplicate", "You already published this.");
  const id = newId(), ts = iso(t);
  await db.batch([
    db.prepare(
      `INSERT INTO posts (id, author_id, title, description, game, category, tags, content, sections, content_hash, status, created_utc, updated_utc)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, 'visible', ?11, ?11)`,
    ).bind(id, u.id, input.title, input.description, input.game, input.category, JSON.stringify(input.tags), content, JSON.stringify(sections(input.content)), hash, ts),
    db.prepare("INSERT INTO posts_fts (post_id, title, description, tags, game, category) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
      .bind(id, input.title, input.description, input.tags.join(" "), input.game, input.category),
    db.prepare("INSERT INTO activity (user_id, kind, created_utc) VALUES (?1, 'post', ?2)").bind(u.id, ts),
  ]);
  await attachImages(env, u, id, input.imageIds);
  return (await getPost(env, u, id))!;
}

export async function updatePost(env: Env, lim: Limits, u: User, id: string, raw: unknown) {
  const post = ID_PATTERN.test(id) ? await env.DB!.prepare("SELECT author_id, status FROM posts WHERE id = ?1").bind(id).first() : null;
  if (!post) throw notFound();
  if (post.author_id !== u.id) throw forbidden("Only the author can edit this post.");
  if (u.banned) throw suspended();
  if (post.status === "removed") throw new ApiError(403, "removed", "A moderator removed this post, so it can't be edited.");
  const { input, content, hash } = await validate(env, u, raw, id);
  const db = env.DB!;
  const before = (await db.prepare("SELECT id FROM images WHERE post_id = ?1 AND kind = 'upload' AND deleted_utc IS NULL").bind(id).all()).results.map((r) => r.id as string);
  await db.batch([
    db.prepare(
      `UPDATE posts SET title = ?2, description = ?3, game = ?4, category = ?5, tags = ?6, content = ?7, sections = ?8, content_hash = ?9, updated_utc = ?10 WHERE id = ?1`,
    ).bind(id, input.title, input.description, input.game, input.category, JSON.stringify(input.tags), content, JSON.stringify(sections(input.content)), hash, iso(now())),
    db.prepare("UPDATE posts_fts SET title = ?2, description = ?3, tags = ?4, game = ?5, category = ?6 WHERE post_id = ?1")
      .bind(id, input.title, input.description, input.tags.join(" "), input.game, input.category),
  ]);
  await attachImages(env, u, id, input.imageIds);
  for (const img of before.filter((x) => !input.imageIds.includes(x))) await purgeImages(env, "id = ?1", img);
  await purgeImages(env, "post_id = ?1 AND kind = 'crosshair-preview'", id); // old server-made previews (the app draws them now)
  return (await getPost(env, u, id))!;
}

/** The author (or a moderator) deletes a post: rows and image files go immediately. */
export async function deletePost(env: Env, u: User, id: string) {
  const post = ID_PATTERN.test(id) ? await env.DB!.prepare("SELECT author_id FROM posts WHERE id = ?1").bind(id).first() : null;
  if (!post) throw notFound();
  if (post.author_id !== u.id && !u.isAdmin) throw forbidden("Only the author can delete this post.");
  await purgeImages(env, "post_id = ?1", id);
  const db = env.DB!;
  const st = ["likes", "bookmarks", "helpful", "imports", "reports"].map((t) => db.prepare(`DELETE FROM ${t} WHERE post_id = ?1`).bind(id));
  st.push(db.prepare("DELETE FROM posts_fts WHERE post_id = ?1").bind(id), db.prepare("DELETE FROM posts WHERE id = ?1").bind(id));
  if (u.isAdmin && post.author_id !== u.id) st.push(modLog(env, u, "delete-post", id, null));
  await db.batch(st);
}

// ---- reading ------------------------------------------------------------------------------------------------------

const SUMMARY = `p.id, p.title, p.game, p.category, p.tags, p.created_utc, p.updated_utc, p.likes, p.helpful_yes, p.helpful_no, p.imports, p.sections, p.status,
  u.id AS author_id, u.login, u.avatar_url, u.created_utc AS joined_utc, u.is_banned, json_extract(p.content, '$.crosshair') AS crosshair_json,
  (SELECT id FROM images i WHERE i.post_id = p.id AND i.kind = 'upload' AND i.status = 'approved' AND i.deleted_utc IS NULL
     ORDER BY i.position LIMIT 1) AS thumb_id`;

function summary(r: Row) {
  return {
    id: r.id, title: r.title, game: r.game, category: r.category, tags: JSON.parse(r.tags as string),
    author: { id: r.author_id, login: r.login, avatarUrl: r.avatar_url ?? null, joinedUtc: r.joined_utc, postCount: 0, likesReceived: 0 },
    createdUtc: r.created_utc, updatedUtc: r.updated_utc, likes: r.likes, helpfulYes: r.helpful_yes, helpfulNo: r.helpful_no, imports: r.imports,
    liked: false, bookmarked: false, thumbUrl: r.thumb_id ? `/api/community/media/${r.thumb_id}/thumb` : null, sections: JSON.parse(r.sections as string), status: r.status,
    // The app draws crosshair previews itself from these settings (sharp at any size, no server work).
    crosshair: typeof r.crosshair_json === "string" ? JSON.parse(r.crosshair_json) : null,
  } as Record<string, unknown> & { id: string; author: Record<string, unknown> };
}

async function viewerFlags(env: Env, viewer: User | null, ids: string[]) {
  if (!viewer || ids.length === 0) return { liked: new Set<string>(), bookmarked: new Set<string>() };
  const q = ids.map((_, i) => `?${i + 2}`).join(",");
  const liked = (await env.DB!.prepare(`SELECT post_id FROM likes WHERE user_id = ?1 AND post_id IN (${q})`).bind(viewer.id, ...ids).all()).results;
  const marked = (await env.DB!.prepare(`SELECT post_id FROM bookmarks WHERE user_id = ?1 AND post_id IN (${q})`).bind(viewer.id, ...ids).all()).results;
  return { liked: new Set(liked.map((r) => r.post_id as string)), bookmarked: new Set(marked.map((r) => r.post_id as string)) };
}

async function authorStats(env: Env, a: Record<string, unknown>) {
  const r = await env.DB!.prepare("SELECT COUNT(*) AS n, COALESCE(SUM(likes), 0) AS l FROM posts WHERE author_id = ?1 AND status = 'visible'").bind(a.id).first<{ n: number; l: number }>();
  a.postCount = r?.n ?? 0;
  a.likesReceived = r?.l ?? 0;
}

export async function getPost(env: Env, viewer: User | null, id: string) {
  if (!ID_PATTERN.test(id)) return null;
  const db = env.DB!;
  const r = await db.prepare(`SELECT ${SUMMARY}, p.description, p.content FROM posts p JOIN users u ON u.id = p.author_id WHERE p.id = ?1`).bind(id).first();
  if (!r) return null;
  const owner = viewer?.id === r.author_id, admin = viewer?.isAdmin === true;
  if ((r.status !== "visible" || r.is_banned === 1) && !owner && !admin) return null;
  const d = summary(r);
  d.description = r.description;
  d.content = JSON.parse(r.content as string);
  const imgs = (await db.prepare("SELECT * FROM images WHERE post_id = ?1 AND kind = 'upload' AND deleted_utc IS NULL ORDER BY position").bind(id).all()).results;
  d.images = imgs.filter((i) => i.status === "approved" || owner || admin).map(imageInfo);
  await authorStats(env, d.author);
  const f = await viewerFlags(env, viewer, [id]);
  d.liked = f.liked.has(id);
  d.bookmarked = f.bookmarked.has(id);
  d.myHelpful = null;
  d.reported = false;
  if (viewer) {
    const h = await db.prepare("SELECT helpful FROM helpful WHERE user_id = ?1 AND post_id = ?2").bind(viewer.id, id).first();
    d.myHelpful = h ? h.helpful === 1 : null;
    d.reported = (await db.prepare("SELECT 1 FROM reports WHERE reporter_id = ?1 AND post_id = ?2").bind(viewer.id, id).first()) !== null;
  }
  d.canEdit = owner && r.status !== "removed";
  return d;
}

/** Free text → safe FTS5 query: words only, each a quoted prefix term (no query syntax reaches SQLite). */
export function ftsQuery(q: string | null): string | null {
  const words = (q ?? "").match(/[\p{L}\p{N}]{2,}/gu)?.slice(0, 8) ?? [];
  return words.length === 0 ? null : words.map((w) => `"${w}"*`).join(" ");
}

export async function listPosts(env: Env, viewer: User | null, o: { q?: string | null; game?: string | null; category?: string | null; tag?: string | null; sort?: string | null; page?: number; author?: string | null; bookmarks?: boolean; mine?: boolean }) {
  const page = Math.min(1000, Math.max(1, Math.floor(o.page ?? 1) || 1)), size = 24;
  const where: string[] = [], args: unknown[] = [];
  const arg = (v: unknown) => { args.push(v); return `?${args.length}`; };
  if (o.mine && viewer) { where.push(`p.author_id = ${arg(viewer.id)}`, "p.status <> 'removed'"); }
  else where.push("p.status = 'visible'", "u.is_banned = 0");
  if (o.bookmarks) {
    if (!viewer) return { items: [], page, pageSize: size, total: 0 };
    where.push(`p.id IN (SELECT post_id FROM bookmarks WHERE user_id = ${arg(viewer.id)})`);
  }
  if (o.game && GAMES.includes(o.game)) where.push(`p.game = ${arg(o.game)}`);
  if (o.category && CATEGORIES.includes(o.category)) where.push(`p.category = ${arg(o.category)}`);
  if (o.tag) where.push(`EXISTS (SELECT 1 FROM json_each(p.tags) WHERE value = ${arg(o.tag.trim().toLowerCase())})`);
  if (o.author) where.push(`u.login = ${arg(o.author)} COLLATE NOCASE`);
  const fts = ftsQuery(o.q ?? null);
  if (fts) where.push(`p.id IN (SELECT post_id FROM posts_fts WHERE posts_fts MATCH ${arg(fts)})`);
  const order = o.sort === "top" ? "p.likes DESC, p.created_utc DESC" : o.sort === "helpful" ? "p.helpful_score DESC, p.helpful_yes DESC, p.created_utc DESC"
    : o.sort === "imports" ? "p.imports DESC, p.created_utc DESC" : "p.created_utc DESC";
  const w = where.join(" AND ");
  const db = env.DB!;
  const total = (await db.prepare(`SELECT COUNT(*) AS n FROM posts p JOIN users u ON u.id = p.author_id WHERE ${w}`).bind(...args).first<{ n: number }>())?.n ?? 0;
  const rows = (await db.prepare(`SELECT ${SUMMARY} FROM posts p JOIN users u ON u.id = p.author_id WHERE ${w} ORDER BY ${order} LIMIT ${size} OFFSET ${(page - 1) * size}`).bind(...args).all()).results;
  const items = rows.map(summary);
  const f = await viewerFlags(env, viewer, items.map((i) => i.id));
  for (const i of items) { i.liked = f.liked.has(i.id); i.bookmarked = f.bookmarked.has(i.id); }
  return { items, page, pageSize: size, total };
}

export async function author(env: Env, login: string) {
  const u = await findByLogin(env, login);
  if (!u || u.banned) return null;
  const a: Record<string, unknown> = { id: u.id, login: u.login, avatarUrl: u.avatarUrl, joinedUtc: iso(u.created), postCount: 0, likesReceived: 0 };
  await authorStats(env, a);
  return a;
}

// ---- reactions ------------------------------------------------------------------------------------------------------

async function requireVisible(env: Env, id: string) {
  const r = ID_PATTERN.test(id) ? await env.DB!.prepare("SELECT status, author_id FROM posts WHERE id = ?1").bind(id).first() : null;
  if (!r || r.status !== "visible") throw notFound();
  return r;
}

export async function setLike(env: Env, u: User, id: string, on: boolean) {
  if (u.banned) throw suspended();
  await requireVisible(env, id);
  const db = env.DB!;
  await db.batch([
    on ? db.prepare("INSERT OR IGNORE INTO likes (user_id, post_id, created_utc) VALUES (?1, ?2, ?3)").bind(u.id, id, iso(now()))
      : db.prepare("DELETE FROM likes WHERE user_id = ?1 AND post_id = ?2").bind(u.id, id),
    db.prepare("UPDATE posts SET likes = (SELECT COUNT(*) FROM likes WHERE post_id = ?1) WHERE id = ?1").bind(id),
  ]);
  return getPost(env, u, id);
}

export async function setBookmark(env: Env, u: User, id: string, on: boolean) {
  if (u.banned) throw suspended();
  await requireVisible(env, id);
  const db = env.DB!;
  await (on ? db.prepare("INSERT OR IGNORE INTO bookmarks (user_id, post_id, created_utc) VALUES (?1, ?2, ?3)").bind(u.id, id, iso(now()))
    : db.prepare("DELETE FROM bookmarks WHERE user_id = ?1 AND post_id = ?2").bind(u.id, id)).run();
  return getPost(env, u, id);
}

/** Lower bound of the 95% Wilson interval, so a couple of votes can't outrank many. */
export function wilson(yes: number, no: number): number {
  const n = yes + no;
  if (n === 0) return 0;
  const z = 1.96, p = yes / n;
  return (p + (z * z) / (2 * n) - z * Math.sqrt((p * (1 - p) + (z * z) / (4 * n)) / n)) / (1 + (z * z) / n);
}

export async function setHelpful(env: Env, u: User, id: string, helpful: boolean | null) {
  if (u.banned) throw suspended();
  const p = await requireVisible(env, id);
  if (p.author_id === u.id) throw new ApiError(400, "own_post", "You can't rate your own post.");
  const db = env.DB!;
  await (helpful === null ? db.prepare("DELETE FROM helpful WHERE user_id = ?1 AND post_id = ?2").bind(u.id, id)
    : db.prepare("INSERT INTO helpful (user_id, post_id, helpful) VALUES (?1, ?2, ?3) ON CONFLICT (user_id, post_id) DO UPDATE SET helpful = excluded.helpful")
      .bind(u.id, id, helpful ? 1 : 0)).run();
  const c = await db.prepare("SELECT SUM(helpful = 1) AS y, SUM(helpful = 0) AS n FROM helpful WHERE post_id = ?1").bind(id).first<{ y: number | null; n: number | null }>();
  const y = c?.y ?? 0, n = c?.n ?? 0;
  await db.prepare("UPDATE posts SET helpful_yes = ?2, helpful_no = ?3, helpful_score = ?4 WHERE id = ?1").bind(id, y, n, wilson(y, n)).run();
  return getPost(env, u, id);
}

export async function recordImport(env: Env, u: User, id: string) {
  await requireVisible(env, id);
  const r = await env.DB!.prepare("INSERT OR IGNORE INTO imports (user_id, post_id, created_utc) VALUES (?1, ?2, ?3)").bind(u.id, id, iso(now())).run();
  if (r.meta.changes > 0) await env.DB!.prepare("UPDATE posts SET imports = imports + 1 WHERE id = ?1").bind(id).run();
}

// ---- reports and moderation ---------------------------------------------------------------------------------------

export async function report(env: Env, lim: Limits, u: User, id: string, raw: unknown) {
  if (u.banned) throw suspended();
  const body = (typeof raw === "object" && raw !== null ? raw : {}) as Record<string, unknown>;
  if (typeof body.reason !== "string" || !REPORT_REASONS.includes(body.reason)) throw new ApiError(400, "invalid", "Choose a reason.");
  const p = await requireVisible(env, id);
  if (p.author_id === u.id) throw new ApiError(400, "own_post", "You can't report your own post.");
  const db = env.DB!, t = now();
  const today = await db.prepare("SELECT COUNT(*) AS n FROM activity WHERE user_id = ?1 AND kind = 'report' AND created_utc > ?2")
    .bind(u.id, iso(new Date(t.getTime() - 86400000))).first<{ n: number }>();
  if ((today?.n ?? 0) >= lim.reportsPerDay) throw new ApiError(429, "report_limit", "You've sent a lot of reports today. Moderators will get to them.");
  const ins = await db.prepare("INSERT OR IGNORE INTO reports (id, post_id, reporter_id, reason, details, created_utc) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
    .bind(newId(), id, u.id, body.reason, clean(body.details, 500, true), iso(t)).run();
  if (ins.meta.changes === 0) throw new ApiError(409, "already_reported", "You already reported this post.");
  await db.batch([
    db.prepare("INSERT INTO activity (user_id, kind, created_utc) VALUES (?1, 'report', ?2)").bind(u.id, iso(t)),
    db.prepare(`UPDATE posts SET status = 'hidden' WHERE id = ?1 AND status = 'visible'
                AND (SELECT COUNT(*) FROM reports WHERE post_id = ?1 AND status = 'open') >= ?2`).bind(id, lim.reportHideThreshold),
  ]);
}

const requireAdmin = (u: User) => { if (!u.isAdmin) throw forbidden("Moderators only."); };
const modLog = (env: Env, admin: User, action: string, target: string, note: string | null) =>
  env.DB!.prepare("INSERT INTO mod_log (id, admin_id, action, target, note, created_utc) VALUES (?1, ?2, ?3, ?4, ?5, ?6)").bind(newId(), admin.id, action, target, note, iso(now()));

export async function queue(env: Env, admin: User) {
  requireAdmin(admin);
  const db = env.DB!;
  const reports = (await db.prepare(
    `SELECT r.id, r.post_id, p.title, p.status, u.login, r.reason, r.details, r.created_utc,
            (SELECT COUNT(*) FROM reports r2 WHERE r2.post_id = r.post_id AND r2.status = 'open') AS open_count
     FROM reports r JOIN posts p ON p.id = r.post_id JOIN users u ON u.id = r.reporter_id WHERE r.status = 'open' ORDER BY r.created_utc LIMIT 200`,
  ).all()).results.map((r) => ({
    id: r.id, postId: r.post_id, postTitle: r.title, postStatus: r.status, reporter: r.login, reason: r.reason, details: r.details,
    createdUtc: r.created_utc, openReportsOnPost: r.open_count,
  }));
  const pendingImages = (await db.prepare(
    `SELECT i.*, u.login, p.title FROM images i JOIN users u ON u.id = i.owner_id LEFT JOIN posts p ON p.id = i.post_id
     WHERE i.status = 'pending' AND i.deleted_utc IS NULL ORDER BY i.created_utc LIMIT 200`,
  ).all()).results.map((r) => ({ image: imageInfo(r), owner: r.login, postId: r.post_id ?? null, postTitle: r.title ?? null, createdUtc: r.created_utc }));
  const hiddenPosts = (await db.prepare(`SELECT ${SUMMARY} FROM posts p JOIN users u ON u.id = p.author_id WHERE p.status = 'hidden' ORDER BY p.updated_utc LIMIT 200`).all()).results.map(summary);
  return { reports, pendingImages, hiddenPosts };
}

export async function reviewImage(env: Env, admin: User, id: string, approve: boolean) {
  requireAdmin(admin);
  const r = ID_PATTERN.test(id) ? await env.DB!.prepare("SELECT post_id FROM images WHERE id = ?1 AND deleted_utc IS NULL AND kind = 'upload'").bind(id).first() : null;
  if (!r) throw notFound("Image not found.");
  await env.DB!.batch([
    env.DB!.prepare("UPDATE images SET status = ?2 WHERE id = ?1").bind(id, approve ? "approved" : "rejected"),
    modLog(env, admin, approve ? "approve-image" : "reject-image", id, null),
  ]);
  if (!approve) await purgeImages(env, "id = ?1", id); // rejected files are deleted at once
}

export async function setPostStatus(env: Env, admin: User, id: string, status: "visible" | "removed", note: unknown) {
  requireAdmin(admin);
  if (!ID_PATTERN.test(id)) throw notFound();
  const db = env.DB!;
  const res = await db.batch([
    db.prepare("UPDATE posts SET status = ?2, removed_utc = ?3 WHERE id = ?1").bind(id, status, status === "removed" ? iso(now()) : null),
    db.prepare("UPDATE reports SET status = ?2 WHERE post_id = ?1 AND status = 'open'").bind(id, status === "removed" ? "resolved" : "dismissed"),
    modLog(env, admin, status === "removed" ? "remove-post" : "restore-post", id, clean(note, 300) || null),
  ]);
  if (res[0].meta.changes === 0) throw notFound();
}

export async function dismissReport(env: Env, admin: User, id: string) {
  requireAdmin(admin);
  const r = await env.DB!.prepare("UPDATE reports SET status = 'dismissed' WHERE id = ?1 AND status = 'open'").bind(id).run();
  if (r.meta.changes === 0) throw notFound("Report not found.");
  await modLog(env, admin, "dismiss-report", id, null).run();
}

export async function setUserFlags(env: Env, admin: User, userId: string, raw: unknown) {
  requireAdmin(admin);
  const b = (typeof raw === "object" && raw !== null ? raw : {}) as Record<string, unknown>;
  const banned = typeof b.banned === "boolean" ? b.banned : null, trusted = typeof b.trusted === "boolean" ? b.trusted : null;
  if (userId === admin.id && banned === true) throw new ApiError(400, "invalid", "You can't suspend yourself.");
  const db = env.DB!;
  const st: D1PreparedStatement[] = [];
  if (banned !== null) st.push(db.prepare("UPDATE users SET is_banned = ?2, ban_reason = ?3 WHERE id = ?1").bind(userId, banned ? 1 : 0, banned ? clean(b.reason, 300) : null));
  if (trusted !== null) st.push(db.prepare("UPDATE users SET is_trusted = ?2 WHERE id = ?1").bind(userId, trusted ? 1 : 0));
  if (banned === true) st.push(db.prepare("DELETE FROM sessions WHERE user_id = ?1").bind(userId));
  if (st.length === 0) throw new ApiError(400, "invalid", "Nothing to change.");
  const res = await db.batch([...st, modLog(env, admin, banned !== null ? (banned ? "ban" : "unban") : trusted ? "trust" : "untrust", userId, clean(b.reason, 300) || null)]);
  if (res[0].meta.changes === 0) throw notFound("User not found.");
}

// ---- cleanup (cron) -------------------------------------------------------------------------------------------------

export async function cleanup(env: Env, lim: Limits): Promise<number> {
  if (!env.DB) return 0;
  const t = now(), db = env.DB;
  let n = 0;
  n += await purgeImages(env, "post_id IS NULL AND kind = 'upload' AND created_utc < ?1", iso(new Date(t.getTime() - lim.orphanHours * 3600000)));
  n += await purgeImages(env, "post_id IN (SELECT id FROM posts WHERE status = 'removed' AND removed_utc < ?1)", iso(new Date(t.getTime() - lim.removedRetentionDays * 86400000)));
  n += await purgeImages(env, "post_id IS NOT NULL AND post_id NOT IN (SELECT id FROM posts)");
  n += await purgeImages(env, "kind = 'crosshair-preview'"); // replaced by app-drawn previews
  await db.batch([
    db.prepare("DELETE FROM sessions WHERE expires_utc < ?1").bind(iso(t)),
    db.prepare("DELETE FROM upload_slots WHERE state = 'reserved' AND reserved_utc < ?1").bind(iso(new Date(t.getTime() - lim.reservationMinutes * 60000))),
    db.prepare("DELETE FROM upload_slots WHERE day < ?1").bind(iso(new Date(t.getTime() - 3 * 86400000)).slice(0, 10)),
    db.prepare("DELETE FROM activity WHERE created_utc < ?1").bind(iso(new Date(t.getTime() - 3 * 86400000))),
  ]);
  return n;
}
