# Community Hub: release checklist

Status: **built and tested locally, not deployed.** Nothing below has been done on any public service. Each step that
creates an account, costs money or makes something public needs the owner's go-ahead.

## 0. Chosen hosting: Cloudflare (serverless)
The Community Hub API runs in the same Cloudflare Worker as the website (`worker/`, `wrangler.jsonc`): D1 for data, R2
(or KV) for images, Cloudflare Images for re-encoding. **Step-by-step setup: `worker/README.md`.** The .NET server in
`src/MoneyTweaks.Community.Server` stays available for self-hosting; both speak the same API.

- [ ] D1 created, `database_id` in `wrangler.jsonc`, `npm run migrate:remote` done.
- [ ] R2 bucket (or KV namespace) created and private.
- [ ] GitHub OAuth App with Device Flow; `GITHUB_CLIENT_ID` in vars; `GITHUB_CLIENT_SECRET` via `wrangler secret put`.
- [ ] `ADMIN_GITHUB_IDS` set.
- [ ] `/healthz` on the deployed Worker shows nothing missing.
- [ ] Deploying from a branch with the placeholder `database_id` fails: finish the setup before merging into the branch
      Cloudflare deploys from.

## 1. Decisions for the owner
- [ ] Official address: the Worker's workers.dev address (or a custom domain later), to ship as the app's default.
- [ ] Moderators (GitHub numeric ids) and who reviews the image queue daily.
- [ ] Image review mode: keep `pre` (recommended at launch) or `post`.
- [ ] Privacy policy and terms text for the website (what's stored: GitHub id/name/avatar, posts, images, reports; how to
      delete; contact).

## 2. Security
- [ ] Register a **GitHub OAuth App** (owner's account), enable **Device Flow**, no scopes needed. Put the client id and
      secret in the host's secret store (`Community__GitHubClientId`, `Community__GitHubClientSecret`).
- [ ] S3/R2 keys limited to the one bucket; bucket private (no public access).
- [ ] HTTPS only (TLS at the host/proxy); HSTS is sent automatically outside Development.
- [ ] If behind a proxy/CDN, make sure the per-IP rate limit sees the real client IP (configure forwarded headers for the
      proxy's IP only), or rely on the per-account limits.
- [ ] `DevSignIn` off (the server refuses to start otherwise).
- [ ] ImageSharp: currently 3.1.12; NuGet lists five advisories, none reachable here (only PNG/JPEG/WebP decoders,
      metadata parsing off, no TIFF/histogram code). Upgrade to 3.2.x after it has been out two weeks (≥ 23 Oct 2026) and
      re-check advisories; v4 needs a Six Labors licence key (free for open source on request).
- [ ] Back up `community.db` (daily) and the bucket; test a restore.

## 3. Privacy
- [ ] Session tokens are random, stored only as SHA-256 hashes; GitHub tokens are never stored (verified by test).
- [ ] Images are re-encoded: EXIF/GPS removed (verified by test).
- [ ] Hardware and FPS are only what the author types; nothing is read from their PC.
- [ ] Account deletion on request: delete the user's posts (author delete removes images) and the `users` row. A
      self-service "delete my account" button is not built yet.
- [ ] Server logs contain user names and post ids, not tokens or IPs beyond the host's own access logs; set a log retention.

## 4. Functionality (already passing locally)
- [x] Publish / edit / delete with author-only permissions; moderators remove/restore/suspend.
- [x] Image upload (PNG/JPEG/WebP), rejection of SVG/EXE/GIF/HTML/corrupt/oversized/tiny/animated files.
- [x] 2 uploads per account per UTC day, server-side; parallel attempts can't exceed it; failures don't count; deleting
      doesn't refund; resets at 00:00 UTC.
- [x] Generated crosshair previews (free), pending images private until approved, reports hide posts at 3.
- [x] Likes, bookmarks, helpfulness (Wilson-ranked), imports counted once, search, tags, sorting, author pages.
- [x] App: browse, detail with import preview, import into library without applying, publish/edit form, moderation tab.
- [ ] Live GitHub device-flow sign-in (needs the OAuth app; tested only against a simulated GitHub).
- [ ] S3/R2 storage against the real bucket (signing verified against AWS's published example; not run against R2).
- [ ] Load test (e.g. 50 concurrent users) on the chosen host.

## 5. Reliability
- [ ] Health check `/healthz` wired to the host's monitoring.
- [ ] Disk space alert; `MaxTotalStorageBytes` set below the storage plan.
- [ ] Cleanup job runs every 30 min (orphans, removed posts, expired sessions); check its log lines.

## 6. Presentation
- [ ] Set the app's default server address and release the app version that includes it.
- [ ] Website: add the Community Hub feature card, privacy policy and guidelines only after the server is live.
- [ ] First posts must be real ones by real users. No seeded or fake posts, users, likes or ratings.
