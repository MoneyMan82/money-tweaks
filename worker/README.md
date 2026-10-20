# Money Tweaks on Cloudflare: website + Community Hub API

One Worker (`money-tweaks`, configured in `../wrangler.jsonc`) serves:

- **the website** from `docs/`, exactly as before (static assets with `docs/_headers`), and
- **the Community Hub API** at `/api/v1/*`, `/media/*` and `/healthz`, the same API the Money Tweaks app already uses.

Nothing here has been deployed. Until you finish the steps below, the API answers `503 not_configured` and lists
what's missing; the website keeps working.

| Part | Cloudflare product | Free plan | Needs a card? |
|---|---|---|---|
| API code | Workers | 100,000 requests/day, 10 ms CPU per request | No |
| Posts, accounts, quota | D1 (SQLite) | 5 GB, 5 M reads/day, 100 k writes/day | No |
| Image files | **R2** (or **KV** as an alternative) | R2: 10 GB/month; KV: 1 GB, 1,000 writes/day | R2: probably yes (see step 3); KV: no |
| Re-encoding uploads | Images binding | 5,000 unique transformations/month (each upload uses 2); going over is never charged, uploads just pause | No |
| Sign-in | GitHub OAuth App (device flow) | Free | No |

Secrets (the GitHub client secret) are stored with `wrangler secret put`, never in `wrangler.jsonc`, the app or git.

---

## What you need to do (once)

Commands run from the `worker/` folder. They need Node.js 20 or newer; if `node -v` doesn't work, install the LTS
version from nodejs.org first.

### 1. Install the tools and log in to Cloudflare
```
npm ci
npx wrangler login
```
A browser window opens; approve access for your Cloudflare account (the one that hosts the website).

### 2. Create the database
```
npx wrangler d1 create money-tweaks-community
```
It prints a `database_id`. Paste it into `../wrangler.jsonc` in place of `00000000-0000-0000-0000-000000000000`.
Then create the tables:
```
npm run migrate:remote
```

### 3. Create the image storage: choose A or B
**A. R2 (recommended).** In the Cloudflare dashboard open **R2 Object Storage**. If it asks you to add a payment method
before enabling R2, that's Cloudflare's requirement even for the free tier (10 GB-month storage, 1 M Class A and 10 M
Class B operations/month free). Then:
```
npx wrangler r2 bucket create money-tweaks-community-media
```
Keep the bucket **private** (don't turn on a public r2.dev URL or custom domain): images are served only through the
API, which checks moderation and permissions.

**B. Workers KV (no card).** In `../wrangler.jsonc`, delete the `r2_buckets` block and uncomment `kv_namespaces`, then:
```
npx wrangler kv namespace create MEDIA_KV
```
and paste the printed `id`. Limits: 1 GB total and 1,000 writes/day (each upload writes 2). Fine for a start; you
can move to R2 later.

### 4. Images binding
Nothing to create: the `images` binding in `wrangler.jsonc` enables it for this Worker on the Images Free plan.
In the dashboard you can check usage under **Images › Transformations**.

### 5. GitHub sign-in (OAuth App with device flow)
1. GitHub → **Settings → Developer settings → OAuth Apps → New OAuth App**.
2. Application name: `Money Tweaks Community Hub`.
   Homepage URL and Authorization callback URL: your Worker address, e.g. `https://money-tweaks.<your-subdomain>.workers.dev`
   (shown in the Cloudflare dashboard under Workers & Pages › money-tweaks).
3. **Register application**, then tick **Enable Device Flow** and **Update application**.
4. Copy the **Client ID** into `../wrangler.jsonc` → `vars.GITHUB_CLIENT_ID` (it's public).
5. Click **Generate a new client secret** and store it as a Worker secret. Don't paste it anywhere else:
   ```
   npx wrangler secret put GITHUB_CLIENT_SECRET
   ```
   (paste when asked; it isn't echoed).

### 6. Moderators
Find your numeric GitHub id at `https://api.github.com/users/<your-login>` (`"id"`), and put it in
`../wrangler.jsonc` → `vars.ADMIN_GITHUB_IDS` (comma-separated for several moderators).

### 7. Check before deploying
```
npm run typecheck
npm test
npx wrangler deploy --dry-run
```

### 8. Deploy (only when you decide to go live)
```
npx wrangler deploy
```
Then open `https://<your worker address>/healthz`: it should show empty lists under `configured`.
If the site is deployed by Cloudflare's Git integration instead, merging the branch deploys it; finish steps 2–6 first.

### 9. Point the app at it
In Money Tweaks: **Community Hub › Guidelines & server**, enter the Worker address (https). For everyone, the
address becomes the app's default in the next app release.

### 10. Try it for real
Sign in with GitHub in the app, publish a post with one screenshot, approve it as moderator (Moderation tab), check it
shows for a second account, and upload a JPEG taken with a phone to confirm the location/camera data is gone
(download it from the post and inspect its properties).

---

## How it's protected

- **Every write needs a session** from GitHub sign-in; author-only edit/delete; moderator-only `/admin/*`.
- **Upload limit:** 2 successful uploads per account per UTC day (`DAILY_UPLOAD_LIMIT`). Each upload claims a numbered
  slot in `upload_slots (user, day, slot)` with one atomic D1 batch; the primary key makes extra slots impossible, so
  parallel requests can't exceed the limit. A slot is released if the upload fails; deleting an image doesn't refund it.
- **Uploads:** PNG/JPEG/WebP only by file signature (SVG, GIF, executables, HTML refused), size (5 MB) and dimension
  (16–4096 px) limits checked from the header, animated images refused, then re-encoded by Cloudflare Images (full size +
  480 px thumbnail) and stripped of metadata chunks/segments. Transparent images stay PNG; others become JPEG.
- **Moderation:** new screenshots are visible only to their author and moderators until approved (`IMAGE_REVIEW=pre`).
  Three reports hide a post until reviewed. Rejected images and images of deleted posts are deleted from storage;
  unused uploads after 24 h and images of removed posts after 30 days (hourly cron).
- **Spam:** GitHub accounts younger than 7 days can't post or upload; 5 posts and 20 reports per day; at most 2 links;
  duplicates refused. Add a Cloudflare WAF rate-limiting rule on `/api/*` if needed.
- **No test logins in production:** test sign-in (`DEV_SIGN_IN`) only exists in `.dev.vars` and only answers on localhost.

## Local development (no Cloudflare account needed)
```
npm ci
npm run migrate:local
npm run dev          # http://localhost:8787 (website + API, local D1/R2/Images)
npm test             # 33 tests in the Workers runtime
```
For test sign-in locally, create `../.dev.vars` with `DEV_SIGN_IN=true` (it's git-ignored).
