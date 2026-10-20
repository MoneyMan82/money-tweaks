# Money Tweaks Community Hub server

The API behind the app's **Community Hub** page: posts (game settings, crosshairs, gaming profiles, presets), image
uploads, likes, bookmarks, helpfulness ratings, reports and moderation. **It isn't deployed anywhere.** Until a
server address is set in the app (Community Hub › Guidelines & server), the app never contacts one.

## How it works

| Part | Choice | Why |
|---|---|---|
| Runtime | ASP.NET Core (.NET 10), cross-platform | Same stack as the app; reuses the app's crosshair rules and geometry |
| Data | SQLite (WAL) in `DataDirectory` | One file, transactional; the upload quota relies on its atomic transactions |
| Images | Object storage: `s3` (Cloudflare R2, AWS S3, B2, MinIO) or `filesystem` (persistent disk) | Images never go in the database or temp folders |
| Identity | GitHub sign-in (device flow in the app); the server checks the token with GitHub and issues its own session | Real accounts make per-user limits meaningful; no passwords stored |

### Upload safety
- Only PNG, JPEG and WebP, checked by file signature (and against the declared type). SVG, GIF, executables, HTML and
  everything else is refused.
- Size, dimension and pixel-count limits are checked from the header **before** decoding; animated images are refused.
- Decoding uses only the PNG/JPEG/WebP decoders of ImageSharp (managed code) with metadata parsing switched off. Every
  image is re-encoded from pixels: EXIF/GPS, comments, ICC/XMP and trailing data are gone. Transparent images stay PNG
  (crosshairs keep their alpha); others become JPEG. Thumbnails are generated.
- Files are private in storage and served only through `/media/{id}/{full|thumb}`, which checks moderation status and
  permissions, with `nosniff` and a `default-src 'none'` CSP.
- With `ImageReview = "pre"` (default) new uploads are visible only to their author and moderators until approved.
- Unattached uploads are deleted after 24 h; images of deleted posts immediately; of moderator-removed posts after 30 days.

### The daily upload limit (default 2 per account per UTC day)
Each successful upload takes one numbered slot in `upload_slots (user, UTC day, slot)`. The primary key makes more slots
than the limit impossible, and slots are claimed in an `IMMEDIATE` transaction, so parallel requests, retries, page
refreshes, extra sessions or direct API calls can't exceed it. A slot is reserved before processing and released if the
upload fails, so failed uploads don't count. Deleting an image doesn't return the slot. Server-generated crosshair
previews don't use slots. Tested: 16 parallel uploads from two sessions of one account → exactly 2 succeed.

### Spam and moderation
GitHub accounts younger than `MinAccountAgeDays` can't post or upload; posts per day, reports per day and links per
description are limited; duplicate posts are refused; optional blocked terms; per-IP rate limit. Three distinct reports
hide a post until a moderator restores or removes it. Moderators (by GitHub user id) review images, reports and hidden
posts, remove/restore posts and suspend accounts (which also signs them out). Moderator actions are logged.

## Configuration
`appsettings.json` → section `Community`, or environment variables such as `Community__S3__SecretAccessKey`.
Secrets (GitHub client secret, S3 keys) belong in the host's secret store, never in this repository.

| Setting | Default | |
|---|---|---|
| `DataDirectory` | `data` | Persistent volume for `community.db` (and images with `filesystem`) |
| `GitHubClientId` / `GitHubClientSecret` | — | Required outside Development |
| `AdminGitHubIds` | `[]` | Numeric GitHub ids of moderators |
| `DailyUploadLimit` | 2 | Per account per UTC day |
| `MaxUploadBytes` / `MaxImageDimension` / `MinImageDimension` / `MaxImagePixels` | 8 MB / 4096 / 16 / 16.7 MP | |
| `ImageReview` | `pre` | `pre` = approve before public, `post` = visible at once |
| `Storage` | `filesystem` | or `s3` with `S3:Endpoint`, `Bucket`, `Region`, `AccessKeyId`, `SecretAccessKey` |
| `MaxTotalStorageBytes` | 5 GB | Uploads pause when reached |
| `MinAccountAgeDays`, `PostsPerDay`, `ReportsPerDay`, `ReportHideThreshold`, `BlockedTerms`, `RequestsPerMinutePerIp` | 7, 5, 20, 3, [], 240 | |
| `DevSignIn` | false | Local testing only; startup fails if enabled outside Development |

The server refuses to start outside Development without GitHub sign-in, with dev sign-in on, or with incomplete S3 settings.

## Local run (testing only)
```
dotnet run --project src/MoneyTweaks.Community.Server --environment Development --urls http://127.0.0.1:5077
```
In the app: Community Hub › Guidelines & server › `http://127.0.0.1:5077` (plain http is accepted only for this PC).
Test sign-in names starting with `admin` get moderator rights in Development only.

## Tests
`dotnet test tests/MoneyTweaks.Community.Tests`: runs the real server on a random 127.0.0.1 port with a temporary folder.
