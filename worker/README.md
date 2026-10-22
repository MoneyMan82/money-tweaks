# Community Hub in the Money Tweaks website Worker

The Community Hub API runs **inside your existing Cloudflare Worker `money-tweaks`** (the one that serves the
website). There is no second Worker, no R2 bucket and no billing information needed.

- **Website:** every address except `/api/...` is served from `docs/` exactly as before, without running any code.
- **Community Hub API:** `https://<your website address>/api/community/...`
  (`/api/community/health` to test the connection).
- **Data:** your existing **D1 database** gets new tables for accounts, profiles, posts, likes, reports, the
  2-uploads-per-day limit, and the **images themselves** (`media_blobs`). Existing tables aren't touched.
- **Images:** re-encoded with **Cloudflare Images** (free plan, no billing): stored at most 1920 px and 1.8 MB each.
- **Sign-in:** GitHub accounts (the app uses GitHub's device sign-in).

**Status: not deployed. Nothing in your Cloudflare account has been changed.** What I tested on my PC (Cloudflare's
local runtime, local database, local image service, no account): 38 automated tests, the website still being served,
`/api/community/health`, and the Money Tweaks app publishing, uploading (limit enforced), moderating, importing,
editing and deleting against this Worker. **Not tested yet:** your real D1 database, real GitHub sign-in and the real
Cloudflare Images service; step 9 tests those.

---

## Values you'll collect

| # | Value | Where | Goes into | Secret? |
|---|---|---|---|---|
| A | Your website's Worker address, e.g. `https://money-tweaks.antti.workers.dev` | Step 1 | Tell me; it becomes the app's default Hub address | No |
| B | Your D1 **database name** | Step 2 | `wrangler.jsonc` → `database_name` | No |
| C | Your D1 **Database ID** (`xxxxxxxx-xxxx-…`) | Step 2 | `wrangler.jsonc` → `database_id` | No |
| D | GitHub OAuth **Client ID** | Step 4 | `wrangler.jsonc` → `GITHUB_CLIENT_ID` | No |
| E | GitHub OAuth **Client secret** | Step 4 | Cloudflare dashboard → Secret `GITHUB_CLIENT_SECRET` (step 7). **Never in a file, chat or git.** | **Yes** |
| F | Your GitHub **user id** (a number) | Step 4 | `wrangler.jsonc` → `ADMIN_GITHUB_IDS` | No |

---

## Step 1: Find your Worker and its address (value A)
1. Log in at <https://dash.cloudflare.com> → left menu **Compute (Workers)** → **Workers & Pages**.
2. Click **money-tweaks**.
3. **Settings → Domains & Routes**: the `workers.dev` line (or a custom domain if you added one) is value **A**.
   Open it in a browser: it should show your website.
4. While you're there, look at **Settings → Build**:
   - If it shows a connected **Git repository**, Cloudflare deploys automatically when `main` changes ("Git deploys").
   - If not, the site was deployed from a computer with `npx wrangler deploy` ("manual deploys").
   Remember which one; step 8 depends on it.

## Step 2: Find your D1 database (values B and C)
Left menu → **Storage & Databases → D1 SQL Database** → click your database. Copy its **name** (value **B**) and the
**Database ID** (value **C**).

## Step 3: Check the database has no conflicting tables (important)
On the database page open **Console**, paste this and click **Execute**:
```sql
SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;
```
The Community Hub adds tables named: `users`, `sessions`, `posts`, `posts_fts`, `images`, `media_blobs`,
`upload_slots`, `likes`, `bookmarks`, `helpful`, `imports`, `reports`, `activity`, `mod_log`.
- If **none** of those names is in your list (or the list is empty): continue.
- If **any** of them already exists: **stop and tell me the list.** I'll rename the Hub's tables so nothing collides.
  (Your existing data is never changed either way: the setup only creates tables that don't exist yet.)

## Step 4: GitHub sign-in app (values D, E, F)
1. GitHub → your picture → **Settings → Developer settings → OAuth Apps → New OAuth App**.
2. Application name: `Money Tweaks Community Hub`. Homepage URL and Authorization callback URL: value **A**.
3. **Register application**, tick **Enable Device Flow**, **Update application**.
4. Copy the **Client ID** (value **D**).
5. **Generate a new client secret** and keep it for step 7 (value **E**; GitHub shows it once).
6. Open `https://api.github.com/users/MoneyMan82` and copy the number after `"id":` (value **F**).

## Step 5: Fill in `wrangler.jsonc` (project root)
Change only these four lines (keep the quotes):
```jsonc
"database_name": "<B>",
"database_id": "<C>",
"GITHUB_CLIENT_ID": "<D>",
"ADMIN_GITHUB_IDS": "<F>",
```
None of these are secrets. Or send me A, B, C, D and F and I'll fill them in, set A as the app's default and re-run
all checks.

## Step 6: Add the Community Hub tables to your database
**Dashboard (no installs):** your database → **Console**. Open `worker/migrations/0001_community.sql` in a text
editor, copy everything, paste, **Execute**. Then the same with `worker/migrations/0002_media_blobs.sql`.
If the console refuses several statements at once, use the command line instead.

**Command line (needs Node.js LTS from nodejs.org):**
```
cd worker
npm ci
npx wrangler login
npm run migrate:remote
```
It lists `0001_community.sql` and `0002_media_blobs.sql` with ✅. These only create new tables.

## Step 7: Add the secret (value E)
**Workers & Pages → money-tweaks → Settings → Variables and Secrets → Add**: Type **Secret**, name
`GITHUB_CLIENT_SECRET`, value **E** → **Deploy**. (This saves the secret on the current version; the website keeps
working.) Secrets stay in place on later deploys.

## Step 8: Deploy safely (only after you approve)
The branch with this code is only on my PC right now. When you say go, I'll push it, then:

**If you have Git deploys (step 1.4):**
1. I merge the branch into `main`; Cloudflare builds and deploys automatically (watch **Deployments** for "Success").
   Build settings needed: root directory `/` (the repository root), build command empty, deploy command
   `npx wrangler deploy`. These are the defaults if you haven't changed them.
2. If anything looks wrong: **Deployments → the previous version → Rollback**. The website comes back instantly.

**If you deploy manually (safest: preview first):** from the project folder:
```
npx wrangler login
npx wrangler versions upload
```
It prints a **Version Preview URL**. Open `<preview URL>` (the website) and `<preview URL>/api/community/health`.
If both are fine, make it live:
```
npx wrangler versions deploy
```
(choose the new version, 100%). If anything goes wrong: dashboard → **Deployments → Rollback**.

## Step 9: Test the connection
Open `<A>/api/community/health`. You should see `"ok": true` and:

| Check | If false |
|---|---|
| `database` | "missing" → step 5 (B, C) and redeploy; "tables are missing" → step 6; "different columns" → step 3 |
| `imageStorage` | Same as database (images are stored in D1) |
| `imageProcessing` | The `images` line in `wrangler.jsonc` must be there |
| `githubSignIn` | Step 5 (D) and step 7 (E) |
| `moderators` | Step 5 (F) |

Then in Money Tweaks: **Community Hub → Guidelines & server**, enter value **A**, **Save** → **Sign in with
GitHub** → publish a post with one screenshot → **Moderation** → approve → check it shows for a second GitHub
account. Tell me the result.

---

## Free-plan limits (no billing anywhere)
- Workers: 100,000 requests/day, 10 ms CPU per request (image work runs in Cloudflare Images, not the Worker).
- D1: 5 GB per database including images; 5 million reads and 100,000 writes per day. Uploads pause at
  `MAX_TOTAL_STORAGE_BYTES` (3 GB) to leave room.
- Cloudflare Images: 5,000 unique transformations/month; an upload uses 2–3. Going over is never charged; uploads
  pause with a clear message until next month.

## Protections
- Every change needs a GitHub sign-in session; only authors edit/delete; `/api/community/admin/*` is moderators only.
- **2 successful uploads per account per UTC day**, claimed atomically in D1 (`upload_slots` primary key), so parallel
  requests can't exceed it; failed uploads don't count; deleting doesn't refund; resets 00:00 UTC.
- PNG/JPEG/WebP only (by file signature), 5 MB / 16–4096 px limits, no animations, re-encoded, metadata removed.
  New screenshots are hidden from others until a moderator approves them.
- Images are deleted from D1 with their post, when rejected, after 24 h if never used, and 30 days after a moderator
  removes a post (hourly job).
- Test sign-in exists only with `DEV_SIGN_IN=true` in a local `.dev.vars` file and only on localhost.

## Tweak Truth Index (shared measured results)
- `POST /api/community/evidence` (signed in, opt-in in the app): one sanitized test result. Strictly validated; only
  these fields are stored: tweak id, test kind, game (fixed list), cleaned CPU/GPU model names, RAM size bucket,
  Windows build, app version, per-metric change/verdict/run counts, and the arrival day. No user id, login, IP or time
  of day is stored with a result. Sign-in only feeds a separate per-day counter (`evidence_quota`, 50 shares per
  account per day, removed after two days). The same test (same key) is counted once.
- `GET /api/community/evidence/{tweakId}`: medians and better/worse shares per test kind, overall and by GPU, CPU,
  Windows version and game. A group shows numbers only once it has 5 results.
- `POST /api/community/admin/evidence/{tweakId}/clear` (moderators): removes all shared results for a tweak.
- The tables (`evidence`, `evidence_quota`) are created automatically on first use; `migrations/0003_evidence.sql`
  and `console-setup.sql` contain the same statements if you prefer to add them yourself. Free plan only.

## For developers (local, no account needed)
```
cd worker
npm ci
npm test                 # 38 tests in Cloudflare's runtime
npm run migrate:local
npm run dev              # website + API at http://localhost:8787
npm run check            # typecheck + tests + deploy dry run (uploads nothing)
```
