# Money Tweaks Community Hub API (Cloudflare Worker)

This folder is a **separate Cloudflare Worker**, `money-tweaks-community-api`. It is not your website: the website
(`docs/` and `wrangler.jsonc` in the project root) is unchanged and keeps deploying as before.

What it does: GitHub sign-in, publishing/editing/deleting posts (game settings, crosshairs, gaming profiles, presets),
likes, bookmarks, ratings, reports, moderation, and image uploads with a **server-enforced limit of 2 successful uploads
per account per UTC day**. Data is in **D1**, images in **R2**, uploads are re-encoded with **Cloudflare Images**.

| File | What it is |
|---|---|
| `wrangler.jsonc` | Worker settings: name, database, bucket, variables. **You fill in 3 values here.** |
| `migrations/0001_community.sql` | Creates the database tables. |
| `src/` | The Worker code. |
| `test/` | Tests that run locally in Cloudflare's runtime (no account needed). |
| `package.json` / `package-lock.json` | Exact tool versions. |

**Status: nothing is deployed and nothing exists in your Cloudflare account yet.** Everything below is still to do.
What *has* been tested: 34 automated tests and the Money Tweaks app against this Worker running on my PC
(`wrangler dev`, local database/bucket/images). Not tested yet: real GitHub sign-in, your real D1/R2, the real
Cloudflare Images service. Step 10 tests those.

---

## Values you'll collect (write them down as you go)

| # | Value | Where you get it | Where it goes | Secret? |
|---|---|---|---|---|
| A | Your **workers.dev subdomain** (e.g. `antti`) | Cloudflare dashboard, step 2 | Builds the API address | No |
| B | **D1 database ID** (looks like `a1b2c3d4-…`) | Step 3 | `wrangler.jsonc` → `database_id` | No |
| C | **GitHub OAuth Client ID** (looks like `Ov23li…`) | Step 5 | `wrangler.jsonc` → `GITHUB_CLIENT_ID` | No |
| D | **GitHub OAuth Client secret** | Step 5 | Cloudflare dashboard → Worker → Secret `GITHUB_CLIENT_SECRET` (step 9) | **Yes: never in a file, never in chat, never in git** |
| E | **Your GitHub user id** (a number) | Step 5 | `wrangler.jsonc` → `ADMIN_GITHUB_IDS` | No |

Your API address will be: `https://money-tweaks-community-api.<A>.workers.dev`

---

## Step 1: Cloudflare account
Use the same Cloudflare account as your website. Log in at <https://dash.cloudflare.com>.

## Step 2: Find your workers.dev subdomain (value A)
Left menu → **Compute (Workers)** → **Workers & Pages**. On the right side ("Account details") you'll see
**Subdomain**, e.g. `antti.workers.dev`. The part before `.workers.dev` is value **A**.

## Step 3: Create the database (value B)
1. Left menu → **Storage & Databases** → **D1 SQL Database** → **Create Database**.
2. Name: `money-tweaks-community` (exactly this).
3. Location: leave **Automatic**. Click **Create**.
4. On the database page, copy the **Database ID**. That's value **B**.

## Step 4: Create the image bucket
1. Left menu → **Storage & Databases** → **R2 Object Storage**.
2. If Cloudflare asks you to add a payment method or "purchase" the R2 plan before you can continue, that's
   Cloudflare's requirement for R2 even on the free tier (free each month: 10 GB storage, 1 million uploads/writes,
   10 million reads; no charge below that). If you don't want to do that, stop here and tell me: the API also supports
   Workers KV (no card), which needs a small config change.
3. **Create bucket** → name: `money-tweaks-community-media` (exactly this) → Location: **Automatic** →
   Default storage class: **Standard** → **Create bucket**.
4. **Don't** turn on "Public access" / r2.dev URL / custom domain. Images are served only through the API, which
   checks moderation and permissions.

## Step 5: GitHub sign-in app (values C, D, E)
1. On GitHub: your picture (top right) → **Settings** → **Developer settings** (bottom of the left menu) →
   **OAuth Apps** → **New OAuth App**.
2. Fill in:
   - Application name: `Money Tweaks Community Hub`
   - Homepage URL: `https://money-tweaks-community-api.<A>.workers.dev`
   - Authorization callback URL: the same address
3. **Register application**.
4. Tick **Enable Device Flow** and click **Update application**. (The app signs in with a short code, so this is required.)
5. Copy the **Client ID**: value **C**.
6. Click **Generate a new client secret**. Copy it somewhere safe for step 9: value **D**. GitHub shows it only once.
7. Open `https://api.github.com/users/<your GitHub login>` in a browser and copy the number after `"id":`: value **E**.

## Step 6: Put B, C and E into `community-api/wrangler.jsonc`
Open the file and change only these three lines (keep the quotes):
```jsonc
"database_id": "<B>",
"GITHUB_CLIENT_ID": "<C>",
"ADMIN_GITHUB_IDS": "<E>",
```
These three are not secrets, so they can be committed to GitHub. (Or send me B, C and E and I'll do it and re-run the checks.)

## Step 7: Create the tables in the database
**Option 1, dashboard (no installs):** Storage & Databases → D1 SQL Database → `money-tweaks-community` → **Console**.
Open `community-api/migrations/0001_community.sql` in a text editor, copy everything, paste it into the console and
click **Execute**. If the console complains about running several statements at once, use option 2.

**Option 2, command line (recommended if you can install Node.js LTS from nodejs.org):** in a terminal:
```
cd community-api
npm ci
npx wrangler login
npx wrangler d1 migrations apply DB --remote
```
`wrangler login` opens your browser to approve access. The last command lists `0001_community.sql` with a ✅.

## Step 8: Connect your GitHub repository (this deploys)
Only do this when you've decided to go live, and after the `community-api` folder is on your repository's `main`
branch (it's on the local branch `community-hub` now; I'll push/merge it when you approve).

1. Cloudflare dashboard → **Workers & Pages** → **Create** → **Import a repository** (under "Workers").
2. **Connect GitHub** and allow Cloudflare access to `MoneyMan82/money-tweaks` only.
3. Select the repository and fill in:
   - **Project name:** `money-tweaks-community-api` (must match `name` in `wrangler.jsonc`)
   - **Build command:** leave empty
   - **Deploy command:** `npx wrangler deploy`
   - **Advanced settings → Path / Root directory:** `community-api`
   - **Production branch:** `main`
4. **Create and deploy.** The first build starts; wait until it says **Success**.
5. In the new Worker → **Settings** → **Build**: set **Build watch paths** to include `community-api/*`, so website
   changes don't redeploy the API. Turn **off** builds for non-production branches if you see that option.

Your website's own Worker isn't touched by this: it stays connected/deployed exactly as it is now.

(Alternative without GitHub: from the `community-api` folder run `npx wrangler deploy` after step 7's option 2.)

## Step 9: Add the secret (value D)
Worker `money-tweaks-community-api` → **Settings** → **Variables and Secrets** → **Add**:
- Type: **Secret**
- Variable name: `GITHUB_CLIENT_SECRET`
- Value: value **D**
- **Deploy**.

Secrets stay in place when GitHub pushes deploy new versions.

## Step 10: Test the connection
Open `https://money-tweaks-community-api.<A>.workers.dev/health` in a browser. You should see `"ok": true` and
five checks:

| Check | If it says false, fix |
|---|---|
| `database` | "missing" → step 6 database_id / redeploy; "tables are missing" → step 7 |
| `imageStorage` | Bucket name in step 4 must be exactly `money-tweaks-community-media` |
| `imageProcessing` | The `images` line in `wrangler.jsonc` must be present |
| `githubSignIn` | Step 6 (`GITHUB_CLIENT_ID`) and step 9 (secret) |
| `moderators` | Step 6 (`ADMIN_GITHUB_IDS`) |

Then in Money Tweaks: **Community Hub → Guidelines & server**, enter
`https://money-tweaks-community-api.<A>.workers.dev`, **Save**, **Sign in with GitHub**, publish a post with one
screenshot, open **Moderation** and approve it, and check it shows when signed in with another GitHub account. Tell me
the results and I'll set the address as the app's default for the next release.

---

## Limits and costs (free plans)
- Workers: 100,000 requests/day; 10 ms CPU per request (the code is built for that: image work runs in Cloudflare Images).
- D1: 5 GB, 5 million reads and 100,000 writes per day.
- R2: 10 GB-month storage free; images are served through the Worker, so there are no public egress costs.
- Cloudflare Images: 5,000 unique transformations/month on the free plan. Each upload uses 2 (full size + thumbnail),
  so about 2,500 uploads a month. Going over is never charged: uploads pause with a clear message until next month.

## How the protections work
- Every write needs a session from GitHub sign-in; only authors edit/delete their posts; `/api/v1/admin/*` is
  moderators only (value E).
- **2 uploads per account per UTC day:** each upload claims a numbered slot in `upload_slots (user, day, slot)` in one
  atomic D1 step; the primary key makes a third slot impossible, so parallel requests or direct API calls can't
  bypass it. Failed uploads release their slot; deleting an image doesn't give it back. Resets at 00:00 UTC.
- Uploads: PNG/JPEG/WebP only (checked by file signature, not name), 5 MB and 16–4096 px limits, no animations,
  re-encoded by Cloudflare Images, metadata removed. New screenshots are visible to others only after a moderator
  approves them.
- Images are deleted from R2 when a post is deleted or an image rejected; unused uploads after 24 h; removed posts'
  images after 30 days (hourly job).
- Test sign-in exists only for local development (`.dev.vars`) and only answers on localhost.

## For developers
```
npm ci
npm run typecheck
npm test                 # 34 tests in the Workers runtime, local D1/R2/Images
npm run migrate:local && npx wrangler dev --local   # http://localhost:8787
npm run check            # typecheck + tests + deploy dry run (uploads nothing)
```
Local test sign-in: create `community-api/.dev.vars` containing `DEV_SIGN_IN=true` (git-ignored).
