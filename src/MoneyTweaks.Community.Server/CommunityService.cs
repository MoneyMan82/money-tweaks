using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MoneyTweaks.Core.Crosshair;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MoneyTweaks.Community.Server;

/// <summary>An expected failure with an HTTP status and a message that's safe to show to the user.</summary>
public sealed class CommunityError(int status, string code, string message, object? extra = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public object? Extra { get; } = extra;
}

/// <summary>
/// Posts, images, reactions and moderation. Every method takes the acting user and checks permissions itself, so the
/// rules hold no matter which endpoint (or which client) calls it.
/// </summary>
public sealed class CommunityService(Database db, CommunityOptions o, TimeProvider clock, IObjectStore store, ImagePipeline images,
    QuotaService quota, AuthService auth, ILogger<CommunityService> log)
{
    private static readonly JsonSerializerOptions Json = new(Api.Json) { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- images ---------------------------------------------------------------------------------------------------

    public async Task<UploadResult> UploadAsync(AuthUser u, byte[] data, string? contentType, CancellationToken ct)
    {
        if (auth.CannotContribute(u) is { } why) throw new CommunityError(403, "not_allowed", why);
        using (var c = db.Open())
        {
            var total = Convert.ToInt64(Database.Scalar(c, "SELECT COALESCE(SUM(bytes + thumb_bytes), 0) FROM images WHERE deleted_utc IS NULL"));
            if (total + data.Length * 2L > o.MaxTotalStorageBytes)
                throw new CommunityError(507, "storage_full", "Image uploads are paused because the Community Hub's storage is full. Try again later.");
        }
        var slot = quota.TryReserve(u.Id) ?? throw new CommunityError(429, "quota_exceeded",
            $"You've used today's {o.DailyUploadLimit} image uploads. The limit resets at 00:00 UTC.", quota.Info(u.Id));
        string? fullKey = null, thumbKey = null;
        try
        {
            var p = await images.ProcessAsync(data, contentType, ct);
            var id = Database.NewId();
            fullKey = $"img/{id}.{p.Ext}";
            thumbKey = $"thumb/{id}.{p.ThumbExt}";
            await store.PutAsync(fullKey, p.Full, p.FullMime, ct);
            await store.PutAsync(thumbKey, p.Thumb, p.ThumbMime, ct);
            var status = o.ImageReview == "post" || u.Trusted || u.IsAdmin ? "approved" : "pending";
            using (var c = db.Open())
                Database.Exec(c, """
                    INSERT INTO images (id, owner_id, post_id, kind, status, width, height, has_alpha, mime, bytes, thumb_mime, thumb_bytes, storage_key, thumb_key, created_utc)
                    VALUES ($id, $o, NULL, 'upload', $st, $w, $h, $a, $m, $b, $tm, $tb, $k, $tk, $now)
                    """,
                    ("$id", id), ("$o", u.Id), ("$st", status), ("$w", p.Width), ("$h", p.Height), ("$a", p.HasAlpha ? 1 : 0), ("$m", p.FullMime),
                    ("$b", p.Full.Length), ("$tm", p.ThumbMime), ("$tb", p.Thumb.Length), ("$k", fullKey), ("$tk", thumbKey), ("$now", Database.Utc(Now)));
            quota.Commit(slot, id);
            log.LogInformation("Image {Id} uploaded by {User} ({W}x{H}, {Status})", id, u.Login, p.Width, p.Height, status);
            return new UploadResult { Image = ImageOf(id)!, Quota = quota.Info(u.Id) };
        }
        catch (Exception ex)
        {
            quota.Release(slot); // a failed upload never uses the allowance
            foreach (var k in new[] { fullKey, thumbKey })
                if (k is not null) try { await store.DeleteAsync(k, CancellationToken.None); } catch { }
            if (ex is ImageRejected r) throw new CommunityError(r.Status, r.Code, r.Message, quota.Info(u.Id));
            if (ex is OperationCanceledException) throw;
            log.LogError(ex, "Upload failed");
            throw new CommunityError(500, "upload_failed", "The image couldn't be saved. It didn't count toward your daily limit.", quota.Info(u.Id));
        }
    }

    public ImageInfo? ImageOf(string id)
    {
        using var c = db.Open();
        using var cmd = Database.Cmd(c, "SELECT id, width, height, kind, status, has_alpha FROM images WHERE id = $id AND deleted_utc IS NULL", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadImage(r) : null;
    }

    private static ImageInfo ReadImage(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Width = r.GetInt32(1), Height = r.GetInt32(2), Kind = r.GetString(3), Status = r.GetString(4),
        HasAlpha = r.GetInt64(5) != 0, Url = $"/media/{r.GetString(0)}/full", ThumbUrl = $"/media/{r.GetString(0)}/thumb",
    };

    /// <summary>The owner deletes an upload (attached or not). The daily slot stays used.</summary>
    public async Task DeleteImageAsync(AuthUser u, string id)
    {
        string? owner;
        using (var c = db.Open()) owner = Database.Scalar(c, "SELECT owner_id FROM images WHERE id = $id AND deleted_utc IS NULL AND kind = 'upload'", ("$id", id)) as string;
        if (owner is null || owner != u.Id && !u.IsAdmin) throw new CommunityError(404, "not_found", "Image not found.");
        await PurgeImagesAsync("id = $id", ("$id", id));
    }

    /// <summary>Marks images deleted, then removes their files. Used by deletion, moderation and cleanup.</summary>
    private async Task<int> PurgeImagesAsync(string where, params (string, object?)[] args)
    {
        var keys = new List<string>();
        var ids = new List<string>();
        using (var c = db.Open())
        {
            using (var cmd = Database.Cmd(c, $"SELECT id, storage_key, thumb_key FROM images WHERE deleted_utc IS NULL AND ({where})", args))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) { ids.Add(r.GetString(0)); keys.Add(r.GetString(1)); keys.Add(r.GetString(2)); }
            foreach (var id in ids) Database.Exec(c, "UPDATE images SET deleted_utc = $now WHERE id = $id", ("$now", Database.Utc(Now)), ("$id", id));
        }
        foreach (var k in keys)
            try { await store.DeleteAsync(k); } catch (Exception ex) { log.LogWarning(ex, "Couldn't delete {Key}; cleanup will retry", k); }
        return ids.Count;
    }

    /// <summary>Image bytes for a viewer, or null if it doesn't exist or this viewer may not see it.</summary>
    public async Task<(byte[] data, string mime, bool publicCache)?> MediaAsync(AuthUser? viewer, string id, string variant, CancellationToken ct)
    {
        if (!PostRules.IdPattern().IsMatch(id) || variant is not ("full" or "thumb")) return null;
        string key, mime, owner, status;
        string? postStatus;
        using (var c = db.Open())
        using (var cmd = Database.Cmd(c, """
            SELECT i.storage_key, i.thumb_key, i.mime, i.thumb_mime, i.owner_id, i.status, p.status
            FROM images i LEFT JOIN posts p ON p.id = i.post_id WHERE i.id = $id AND i.deleted_utc IS NULL
            """, ("$id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            key = variant == "full" ? r.GetString(0) : r.GetString(1);
            mime = variant == "full" ? r.GetString(2) : r.GetString(3);
            owner = r.GetString(4);
            status = r.GetString(5);
            postStatus = r.IsDBNull(6) ? null : r.GetString(6);
        }
        bool publicOk = status == "approved" && postStatus == "visible";
        bool privileged = viewer is not null && (viewer.Id == owner || viewer.IsAdmin);
        if (!publicOk && !privileged) return null;
        var data = await store.GetAsync(key, ct);
        return data is null ? null : (data, mime, publicOk);
    }

    // ---- posts ------------------------------------------------------------------------------------------------------

    public async Task<PostDetail> CreatePostAsync(AuthUser u, PostInput input, CancellationToken ct)
    {
        if (auth.CannotContribute(u) is { } why) throw new CommunityError(403, "not_allowed", why);
        var (content, hash) = ValidateInput(u, input, postId: null);
        var id = Database.NewId();
        var now = Database.Utc(Now);
        using (var c = db.Open())
        {
            using var tx = c.BeginTransaction(deferred: false);
            var recent = Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM activity WHERE user_id = $u AND kind = 'post' AND created_utc > $since",
                ("$u", u.Id), ("$since", Database.Utc(Now.AddDays(-1)))));
            if (recent >= o.PostsPerDay) throw new CommunityError(429, "post_limit", $"You can publish up to {o.PostsPerDay} posts per day.");
            if (Database.Scalar(c, "SELECT 1 FROM posts WHERE author_id = $u AND content_hash = $h AND created_utc > $since",
                ("$u", u.Id), ("$h", hash), ("$since", Database.Utc(Now.AddDays(-1)))) is not null)
                throw new CommunityError(409, "duplicate", "You already published this.");
            Database.Exec(c, """
                INSERT INTO posts (id, author_id, title, description, game, category, tags, content, sections, content_hash, status, created_utc, updated_utc)
                VALUES ($id, $a, $t, $d, $g, $c, $tags, $content, $sections, $h, 'visible', $now, $now)
                """,
                ("$id", id), ("$a", u.Id), ("$t", input.Title), ("$d", input.Description), ("$g", input.Game), ("$c", input.Category),
                ("$tags", JsonSerializer.Serialize(input.Tags)), ("$content", content), ("$sections", JsonSerializer.Serialize(input.Content.Sections())),
                ("$h", hash), ("$now", now));
            Database.Exec(c, "INSERT INTO posts_fts (post_id, title, description, tags, game, category) VALUES ($id, $t, $d, $tags, $g, $c)",
                ("$id", id), ("$t", input.Title), ("$d", input.Description), ("$tags", string.Join(" ", input.Tags)), ("$g", input.Game), ("$c", input.Category));
            AttachImages(c, u, id, input.ImageIds);
            Database.Exec(c, "INSERT INTO activity (user_id, kind, created_utc) VALUES ($u, 'post', $now)", ("$u", u.Id), ("$now", now));
            tx.Commit();
        }
        await RegeneratePreviewAsync(id, ct);
        log.LogInformation("Post {Id} published by {User}", id, u.Login);
        return GetPost(u, id)!;
    }

    public async Task<PostDetail> UpdatePostAsync(AuthUser u, string id, PostInput input, CancellationToken ct)
    {
        var author = AuthorOf(id) ?? throw new CommunityError(404, "not_found", "Post not found.");
        if (author != u.Id) throw new CommunityError(403, "forbidden", "Only the author can edit this post.");
        if (u.Banned) throw new CommunityError(403, "not_allowed", "This account is suspended from the Community Hub.");
        var (content, hash) = ValidateInput(u, input, id);
        List<string> dropped;
        using (var c = db.Open())
        {
            using var tx = c.BeginTransaction(deferred: false);
            var status = Database.Scalar(c, "SELECT status FROM posts WHERE id = $id", ("$id", id)) as string;
            if (status == "removed") throw new CommunityError(403, "removed", "A moderator removed this post, so it can't be edited.");
            Database.Exec(c, """
                UPDATE posts SET title = $t, description = $d, game = $g, category = $c, tags = $tags, content = $content, sections = $sections,
                    content_hash = $h, updated_utc = $now WHERE id = $id
                """,
                ("$id", id), ("$t", input.Title), ("$d", input.Description), ("$g", input.Game), ("$c", input.Category),
                ("$tags", JsonSerializer.Serialize(input.Tags)), ("$content", content), ("$sections", JsonSerializer.Serialize(input.Content.Sections())),
                ("$h", hash), ("$now", Database.Utc(Now)));
            Database.Exec(c, "UPDATE posts_fts SET title = $t, description = $d, tags = $tags, game = $g, category = $c WHERE post_id = $id",
                ("$id", id), ("$t", input.Title), ("$d", input.Description), ("$tags", string.Join(" ", input.Tags)), ("$g", input.Game), ("$c", input.Category));
            dropped = [];
            using (var cmd = Database.Cmd(c, "SELECT id FROM images WHERE post_id = $p AND kind = 'upload' AND deleted_utc IS NULL", ("$p", id)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) if (!input.ImageIds.Contains(r.GetString(0))) dropped.Add(r.GetString(0));
            AttachImages(c, u, id, input.ImageIds);
            tx.Commit();
        }
        foreach (var img in dropped) await PurgeImagesAsync("id = $id", ("$id", img));
        await RegeneratePreviewAsync(id, ct);
        return GetPost(u, id)!;
    }

    /// <summary>The author deletes their post: everything is removed, including image files. Moderators use RemovePost instead.</summary>
    public async Task DeletePostAsync(AuthUser u, string id)
    {
        var author = AuthorOf(id) ?? throw new CommunityError(404, "not_found", "Post not found.");
        if (author != u.Id && !u.IsAdmin) throw new CommunityError(403, "forbidden", "Only the author can delete this post.");
        await PurgeImagesAsync("post_id = $p", ("$p", id));
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        foreach (var t in new[] { "likes", "bookmarks", "helpful", "imports", "reports" }) Database.Exec(c, $"DELETE FROM {t} WHERE post_id = $p", ("$p", id));
        Database.Exec(c, "DELETE FROM posts_fts WHERE post_id = $p", ("$p", id));
        Database.Exec(c, "DELETE FROM posts WHERE id = $p", ("$p", id));
        if (u.IsAdmin && author != u.Id) ModLog(c, u, "delete-post", id, null);
        tx.Commit();
        log.LogInformation("Post {Id} deleted by {User}", id, u.Login);
    }

    private string? AuthorOf(string id)
    {
        if (!PostRules.IdPattern().IsMatch(id)) return null;
        using var c = db.Open();
        return Database.Scalar(c, "SELECT author_id FROM posts WHERE id = $id", ("$id", id)) as string;
    }

    private (string content, string hash) ValidateInput(AuthUser u, PostInput input, string? postId)
    {
        if (input is null) throw new CommunityError(400, "invalid", "Missing post.");
        var errors = PostRules.Validate(input);
        if (input.Content.Crosshair is { ValueKind: JsonValueKind.Object } x)
        {
            try
            {
                var profile = x.Deserialize<CrosshairProfile>(Json) ?? throw new JsonException();
                CrosshairSanitizer.Clean(profile);
                foreach (var l in profile.Layers.Where(l => l.Kind == LayerKind.Image))
                    if (l.ImageFile is not { } f || !f.StartsWith("img:", StringComparison.Ordinal) || !input.ImageIds.Contains(f[4..])) l.ImageFile = null;
                input.Content.Crosshair = JsonSerializer.SerializeToElement(profile, Json);
            }
            catch (JsonException) { errors.Add("The crosshair isn't valid."); }
        }
        else input.Content.Crosshair = null;
        var text = string.Join(" ", new[] { input.Title, input.Description, string.Join(" ", input.Tags), input.Content.Settings?.Notes ?? "" });
        foreach (var term in o.BlockedTerms.Where(t => t.Trim().Length > 0))
            if (Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(term.Trim())}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            { errors.Add("The text contains words that aren't allowed in the Community Hub."); break; }
        // Images must be the author's own uploads, not deleted and not used by another post.
        using (var c = db.Open())
            foreach (var img in input.ImageIds)
            {
                using var cmd = Database.Cmd(c, "SELECT owner_id, post_id, kind FROM images WHERE id = $id AND deleted_utc IS NULL", ("$id", img));
                using var r = cmd.ExecuteReader();
                if (!r.Read() || r.GetString(0) != u.Id || r.GetString(2) != "upload" || !r.IsDBNull(1) && r.GetString(1) != postId)
                { errors.Add("One of the images can't be used (it isn't your upload, was deleted, or belongs to another post)."); break; }
            }
        if (errors.Count > 0) throw new CommunityError(400, "invalid", string.Join(" ", errors.Distinct()), errors.Distinct().ToList());
        var content = JsonSerializer.Serialize(input.Content, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Title.ToLowerInvariant() + "\n" + content))).ToLowerInvariant();
        return (content, hash);
    }

    private static void AttachImages(SqliteConnection c, AuthUser u, string postId, List<string> ids)
    {
        for (int i = 0; i < ids.Count; i++)
            Database.Exec(c, "UPDATE images SET post_id = $p, position = $i WHERE id = $id AND owner_id = $u AND kind = 'upload'",
                ("$p", postId), ("$i", i), ("$id", ids[i]), ("$u", u.Id));
    }

    /// <summary>
    /// (Re)creates the generated crosshair preview. It's made by the server from the validated configuration, so it never
    /// uses the upload allowance. Image layers are drawn only when their image is approved.
    /// </summary>
    public async Task RegeneratePreviewAsync(string postId, CancellationToken ct = default)
    {
        string? json, owner;
        using (var c = db.Open())
        using (var cmd = Database.Cmd(c, "SELECT content, author_id FROM posts WHERE id = $id", ("$id", postId)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return;
            json = r.GetString(0);
            owner = r.GetString(1);
        }
        await PurgeImagesAsync("post_id = $p AND kind = 'crosshair-preview'", ("$p", postId));
        var content = JsonSerializer.Deserialize<PostContent>(json, Json);
        if (content?.Crosshair is not { ValueKind: JsonValueKind.Object } x) return;
        var profile = x.Deserialize<CrosshairProfile>(Json);
        if (profile is null) return;
        var loaded = new Dictionary<string, Image<Rgba32>?>();
        try
        {
            foreach (var f in profile.Layers.Where(l => l.Kind == LayerKind.Image && l.ImageFile is not null).Select(l => l.ImageFile!).Distinct())
            {
                var id = f[4..];
                string? key = null;
                using (var c = db.Open())
                    key = Database.Scalar(c, "SELECT storage_key FROM images WHERE id = $id AND post_id = $p AND status = 'approved' AND deleted_utc IS NULL",
                        ("$id", id), ("$p", postId)) as string;
                loaded[f] = key is not null && await store.GetAsync(key, ct) is { } bytes ? Image.Load<Rgba32>(bytes) : null;
            }
            var png = CrosshairPreview.Render(profile, f => loaded.GetValueOrDefault(f));
            var pid = Database.NewId();
            await store.PutAsync($"img/{pid}.png", png, "image/png", ct);
            await store.PutAsync($"thumb/{pid}.png", png, "image/png", ct);
            using var c2 = db.Open();
            Database.Exec(c2, """
                INSERT INTO images (id, owner_id, post_id, kind, status, width, height, has_alpha, mime, bytes, thumb_mime, thumb_bytes, storage_key, thumb_key, position, created_utc)
                VALUES ($id, $o, $p, 'crosshair-preview', 'approved', $s, $s, 0, 'image/png', $b, 'image/png', $b, $k, $tk, -1, $now)
                """,
                ("$id", pid), ("$o", owner), ("$p", postId), ("$s", CrosshairPreview.Size), ("$b", png.Length), ("$k", $"img/{pid}.png"), ("$tk", $"thumb/{pid}.png"), ("$now", Database.Utc(Now)));
        }
        finally { foreach (var i in loaded.Values) i?.Dispose(); }
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    private const string SummaryColumns = """
        p.id, p.title, p.game, p.category, p.tags, p.created_utc, p.updated_utc, p.likes, p.helpful_yes, p.helpful_no, p.imports, p.sections, p.status,
        u.id, u.login, u.avatar_url, u.created_utc,
        (SELECT id FROM images i WHERE i.post_id = p.id AND i.status = 'approved' AND i.deleted_utc IS NULL ORDER BY CASE WHEN i.kind = 'upload' THEN 0 ELSE 1 END, i.position LIMIT 1)
        """;

    private PostSummary ReadSummary(SqliteDataReader r, PostSummary s)
    {
        s.Id = r.GetString(0); s.Title = r.GetString(1); s.Game = r.GetString(2); s.Category = r.GetString(3);
        s.Tags = JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [];
        s.CreatedUtc = Database.ParseUtc(r.GetString(5)); s.UpdatedUtc = Database.ParseUtc(r.GetString(6));
        s.Likes = r.GetInt32(7); s.HelpfulYes = r.GetInt32(8); s.HelpfulNo = r.GetInt32(9); s.Imports = r.GetInt32(10);
        s.Sections = JsonSerializer.Deserialize<List<string>>(r.GetString(11)) ?? [];
        s.Status = r.GetString(12);
        s.Author = new AuthorInfo { Id = r.GetString(13), Login = r.GetString(14), AvatarUrl = r.IsDBNull(15) ? null : r.GetString(15), JoinedUtc = Database.ParseUtc(r.GetString(16)) };
        s.ThumbUrl = r.IsDBNull(17) ? null : $"/media/{r.GetString(17)}/thumb";
        return s;
    }

    private void FillViewerFlags(SqliteConnection c, AuthUser? viewer, PostSummary s)
    {
        if (viewer is null) return;
        s.Liked = Database.Scalar(c, "SELECT 1 FROM likes WHERE user_id = $u AND post_id = $p", ("$u", viewer.Id), ("$p", s.Id)) is not null;
        s.Bookmarked = Database.Scalar(c, "SELECT 1 FROM bookmarks WHERE user_id = $u AND post_id = $p", ("$u", viewer.Id), ("$p", s.Id)) is not null;
    }

    public PostDetail? GetPost(AuthUser? viewer, string id)
    {
        if (!PostRules.IdPattern().IsMatch(id)) return null;
        using var c = db.Open();
        var d = new PostDetail();
        string authorId;
        using (var cmd = Database.Cmd(c, $"SELECT {SummaryColumns}, p.description, p.content, u.is_banned FROM posts p JOIN users u ON u.id = p.author_id WHERE p.id = $id", ("$id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            ReadSummary(r, d);
            d.Description = r.GetString(18);
            d.Content = JsonSerializer.Deserialize<PostContent>(r.GetString(19), Json) ?? new();
            authorId = d.Author.Id;
            bool privileged = viewer is not null && (viewer.Id == authorId || viewer.IsAdmin);
            if ((d.Status != "visible" || r.GetInt64(20) != 0) && !privileged) return null;
        }
        bool owner = viewer?.Id == authorId, admin = viewer?.IsAdmin == true;
        using (var cmd = Database.Cmd(c, "SELECT id, width, height, kind, status, has_alpha FROM images WHERE post_id = $p AND deleted_utc IS NULL ORDER BY position", ("$p", id)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var img = ReadImage(r);
                if (img.Status == "approved" || owner || admin) d.Images.Add(img);
            }
        FillAuthorStats(c, d.Author);
        FillViewerFlags(c, viewer, d);
        if (viewer is not null)
        {
            var h = Database.Scalar(c, "SELECT helpful FROM helpful WHERE user_id = $u AND post_id = $p", ("$u", viewer.Id), ("$p", id));
            d.MyHelpful = h is null ? null : Convert.ToInt64(h) != 0;
            d.Reported = Database.Scalar(c, "SELECT 1 FROM reports WHERE reporter_id = $u AND post_id = $p", ("$u", viewer.Id), ("$p", id)) is not null;
        }
        d.CanEdit = owner && d.Status != "removed";
        return d;
    }

    private static void FillAuthorStats(SqliteConnection c, AuthorInfo a)
    {
        a.PostCount = Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM posts WHERE author_id = $u AND status = 'visible'", ("$u", a.Id)));
        a.LikesReceived = Convert.ToInt32(Database.Scalar(c, "SELECT COALESCE(SUM(likes), 0) FROM posts WHERE author_id = $u AND status = 'visible'", ("$u", a.Id)));
    }

    public PostList ListPosts(AuthUser? viewer, string? q, string? game, string? category, string? tag, string? sort, int page,
        string? authorLogin = null, bool bookmarks = false, bool mine = false)
    {
        page = Math.Clamp(page, 1, 1000);
        const int size = 24;
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (mine && viewer is not null) { where.Add("p.author_id = $me"); args.Add(("$me", viewer.Id)); where.Add("p.status <> 'removed'"); }
        else { where.Add("p.status = 'visible'"); where.Add("u.is_banned = 0"); }
        if (bookmarks)
        {
            if (viewer is null) return new PostList { Page = page, PageSize = size };
            where.Add("p.id IN (SELECT post_id FROM bookmarks WHERE user_id = $bu)"); args.Add(("$bu", viewer.Id));
        }
        if (!string.IsNullOrWhiteSpace(game) && Taxonomy.Games.Contains(game)) { where.Add("p.game = $game"); args.Add(("$game", game)); }
        if (!string.IsNullOrWhiteSpace(category) && Taxonomy.Categories.Contains(category)) { where.Add("p.category = $cat"); args.Add(("$cat", category)); }
        if (!string.IsNullOrWhiteSpace(tag)) { where.Add("EXISTS (SELECT 1 FROM json_each(p.tags) WHERE value = $tag)"); args.Add(("$tag", tag.Trim().ToLowerInvariant())); }
        if (!string.IsNullOrWhiteSpace(authorLogin)) { where.Add("u.login = $al COLLATE NOCASE"); args.Add(("$al", authorLogin)); }
        if (FtsQuery(q) is { } fts) { where.Add("p.id IN (SELECT post_id FROM posts_fts WHERE posts_fts MATCH $q)"); args.Add(("$q", fts)); }
        var order = sort switch
        {
            "top" => "p.likes DESC, p.created_utc DESC",
            "helpful" => "p.helpful_score DESC, p.helpful_yes DESC, p.created_utc DESC",
            "imports" => "p.imports DESC, p.created_utc DESC",
            _ => "p.created_utc DESC",
        };
        var w = string.Join(" AND ", where);
        using var c = db.Open();
        var list = new PostList { Page = page, PageSize = size };
        list.Total = Convert.ToInt32(Database.Scalar(c, $"SELECT COUNT(*) FROM posts p JOIN users u ON u.id = p.author_id WHERE {w}", args.ToArray()));
        args.Add(("$off", (page - 1) * size));
        using (var cmd = Database.Cmd(c, $"SELECT {SummaryColumns} FROM posts p JOIN users u ON u.id = p.author_id WHERE {w} ORDER BY {order} LIMIT {size} OFFSET $off", args.ToArray()))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) list.Items.Add(ReadSummary(r, new PostSummary()));
        foreach (var s in list.Items) FillViewerFlags(c, viewer, s);
        return list;
    }

    /// <summary>Turns free text into a safe FTS5 query: words only, each as a quoted prefix term.</summary>
    public static string? FtsQuery(string? q)
    {
        var words = Regex.Matches(q ?? "", @"[\p{L}\p{N}]{2,}").Select(m => m.Value).Take(8).ToList();
        return words.Count == 0 ? null : string.Join(" ", words.Select(wd => "\"" + wd + "\"*"));
    }

    public AuthorInfo? Author(string login)
    {
        var u = auth.FindByLogin(login);
        if (u is null || u.Banned) return null;
        var a = new AuthorInfo { Id = u.Id, Login = u.Login, AvatarUrl = u.AvatarUrl, JoinedUtc = u.CreatedUtc };
        using var c = db.Open();
        FillAuthorStats(c, a);
        return a;
    }

    // ---- reactions ----------------------------------------------------------------------------------------------------

    private void RequireVisible(SqliteConnection c, string postId)
    {
        if (!PostRules.IdPattern().IsMatch(postId) || Database.Scalar(c, "SELECT status FROM posts WHERE id = $id", ("$id", postId)) as string != "visible")
            throw new CommunityError(404, "not_found", "Post not found.");
    }

    public PostDetail SetLike(AuthUser u, string postId, bool on) => Toggle(u, postId, "likes", on, "UPDATE posts SET likes = (SELECT COUNT(*) FROM likes WHERE post_id = $p) WHERE id = $p");
    public PostDetail SetBookmark(AuthUser u, string postId, bool on) => Toggle(u, postId, "bookmarks", on, null);

    private PostDetail Toggle(AuthUser u, string postId, string table, bool on, string? recount)
    {
        if (u.Banned) throw new CommunityError(403, "not_allowed", "This account is suspended from the Community Hub.");
        using (var c = db.Open())
        {
            RequireVisible(c, postId);
            using var tx = c.BeginTransaction(deferred: false);
            if (on) Database.Exec(c, $"INSERT OR IGNORE INTO {table} (user_id, post_id, created_utc) VALUES ($u, $p, $now)", ("$u", u.Id), ("$p", postId), ("$now", Database.Utc(Now)));
            else Database.Exec(c, $"DELETE FROM {table} WHERE user_id = $u AND post_id = $p", ("$u", u.Id), ("$p", postId));
            if (recount is not null) Database.Exec(c, recount, ("$p", postId));
            tx.Commit();
        }
        return GetPost(u, postId)!;
    }

    /// <summary>"Did this help?" One answer per account, changeable; authors can't rate their own posts.</summary>
    public PostDetail SetHelpful(AuthUser u, string postId, bool? helpful)
    {
        if (u.Banned) throw new CommunityError(403, "not_allowed", "This account is suspended from the Community Hub.");
        if (AuthorOf(postId) == u.Id) throw new CommunityError(400, "own_post", "You can't rate your own post.");
        using (var c = db.Open())
        {
            RequireVisible(c, postId);
            using var tx = c.BeginTransaction(deferred: false);
            if (helpful is null) Database.Exec(c, "DELETE FROM helpful WHERE user_id = $u AND post_id = $p", ("$u", u.Id), ("$p", postId));
            else Database.Exec(c, "INSERT INTO helpful (user_id, post_id, helpful) VALUES ($u, $p, $h) ON CONFLICT(user_id, post_id) DO UPDATE SET helpful = excluded.helpful",
                ("$u", u.Id), ("$p", postId), ("$h", helpful.Value ? 1 : 0));
            long yes = Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM helpful WHERE post_id = $p AND helpful = 1", ("$p", postId)));
            long no = Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM helpful WHERE post_id = $p AND helpful = 0", ("$p", postId)));
            Database.Exec(c, "UPDATE posts SET helpful_yes = $y, helpful_no = $n, helpful_score = $s WHERE id = $p", ("$y", yes), ("$n", no), ("$s", Wilson(yes, no)), ("$p", postId));
            tx.Commit();
        }
        return GetPost(u, postId)!;
    }

    /// <summary>Lower bound of the 95% Wilson interval: a few votes can't outrank many.</summary>
    public static double Wilson(long yes, long no)
    {
        double n = yes + no;
        if (n == 0) return 0;
        const double z = 1.96;
        double p = yes / n;
        return (p + z * z / (2 * n) - z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n)) / (1 + z * z / n);
    }

    public void RecordImport(AuthUser u, string postId)
    {
        using var c = db.Open();
        RequireVisible(c, postId);
        if (Database.Exec(c, "INSERT OR IGNORE INTO imports (user_id, post_id, created_utc) VALUES ($u, $p, $now)", ("$u", u.Id), ("$p", postId), ("$now", Database.Utc(Now))) > 0)
            Database.Exec(c, "UPDATE posts SET imports = imports + 1 WHERE id = $p", ("$p", postId));
    }

    // ---- reports and moderation ---------------------------------------------------------------------------------------

    public void Report(AuthUser u, string postId, ReportInput input)
    {
        if (u.Banned) throw new CommunityError(403, "not_allowed", "This account is suspended from the Community Hub.");
        if (!Taxonomy.ReportReasons.Contains(input?.Reason)) throw new CommunityError(400, "invalid", "Choose a reason.");
        var details = PostRules.Clean(input!.Details, 500, multiline: true);
        if (AuthorOf(postId) == u.Id) throw new CommunityError(400, "own_post", "You can't report your own post.");
        using var c = db.Open();
        RequireVisible(c, postId);
        using var tx = c.BeginTransaction(deferred: false);
        var today = Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM activity WHERE user_id = $u AND kind = 'report' AND created_utc > $since",
            ("$u", u.Id), ("$since", Database.Utc(Now.AddDays(-1)))));
        if (today >= o.ReportsPerDay) throw new CommunityError(429, "report_limit", "You've sent a lot of reports today. Moderators will get to them.");
        if (Database.Exec(c, "INSERT OR IGNORE INTO reports (id, post_id, reporter_id, reason, details, created_utc) VALUES ($id, $p, $u, $r, $d, $now)",
            ("$id", Database.NewId()), ("$p", postId), ("$u", u.Id), ("$r", input.Reason), ("$d", details), ("$now", Database.Utc(Now))) == 0)
            throw new CommunityError(409, "already_reported", "You already reported this post.");
        Database.Exec(c, "INSERT INTO activity (user_id, kind, created_utc) VALUES ($u, 'report', $now)", ("$u", u.Id), ("$now", Database.Utc(Now)));
        var open = Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM reports WHERE post_id = $p AND status = 'open'", ("$p", postId)));
        if (open >= o.ReportHideThreshold)
        {
            Database.Exec(c, "UPDATE posts SET status = 'hidden' WHERE id = $p AND status = 'visible'", ("$p", postId));
            log.LogInformation("Post {Id} hidden after {N} reports, awaiting review", postId, open);
        }
        tx.Commit();
    }

    private static void RequireAdmin(AuthUser u)
    {
        if (!u.IsAdmin) throw new CommunityError(403, "forbidden", "Moderators only.");
    }

    private void ModLog(SqliteConnection c, AuthUser admin, string action, string target, string? note) =>
        Database.Exec(c, "INSERT INTO mod_log (id, admin_id, action, target, note, created_utc) VALUES ($id, $a, $ac, $t, $n, $now)",
            ("$id", Database.NewId()), ("$a", admin.Id), ("$ac", action), ("$t", target), ("$n", note), ("$now", Database.Utc(Now)));

    public AdminQueue Queue(AuthUser admin)
    {
        RequireAdmin(admin);
        var q = new AdminQueue();
        using var c = db.Open();
        using (var cmd = Database.Cmd(c, """
            SELECT r.id, r.post_id, p.title, p.status, u.login, r.reason, r.details, r.created_utc,
                   (SELECT COUNT(*) FROM reports r2 WHERE r2.post_id = r.post_id AND r2.status = 'open')
            FROM reports r JOIN posts p ON p.id = r.post_id JOIN users u ON u.id = r.reporter_id
            WHERE r.status = 'open' ORDER BY r.created_utc LIMIT 200
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                q.Reports.Add(new AdminReport
                {
                    Id = r.GetString(0), PostId = r.GetString(1), PostTitle = r.GetString(2), PostStatus = r.GetString(3), Reporter = r.GetString(4),
                    Reason = r.GetString(5), Details = r.GetString(6), CreatedUtc = Database.ParseUtc(r.GetString(7)), OpenReportsOnPost = r.GetInt32(8),
                });
        using (var cmd = Database.Cmd(c, """
            SELECT i.id, i.width, i.height, i.kind, i.status, i.has_alpha, u.login, i.post_id, p.title, i.created_utc
            FROM images i JOIN users u ON u.id = i.owner_id LEFT JOIN posts p ON p.id = i.post_id
            WHERE i.status = 'pending' AND i.deleted_utc IS NULL ORDER BY i.created_utc LIMIT 200
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                q.PendingImages.Add(new AdminImage
                {
                    Image = ReadImage(r), Owner = r.GetString(6), PostId = r.IsDBNull(7) ? null : r.GetString(7),
                    PostTitle = r.IsDBNull(8) ? null : r.GetString(8), CreatedUtc = Database.ParseUtc(r.GetString(9)),
                });
        using (var cmd = Database.Cmd(c, $"SELECT {SummaryColumns} FROM posts p JOIN users u ON u.id = p.author_id WHERE p.status = 'hidden' ORDER BY p.updated_utc LIMIT 200"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) q.HiddenPosts.Add(ReadSummary(r, new PostSummary()));
        return q;
    }

    public async Task ReviewImageAsync(AuthUser admin, string imageId, bool approve)
    {
        RequireAdmin(admin);
        string? postId;
        using (var c = db.Open())
        {
            postId = Database.Scalar(c, "SELECT post_id FROM images WHERE id = $id AND deleted_utc IS NULL AND kind = 'upload'", ("$id", imageId)) as string;
            if (Database.Exec(c, "UPDATE images SET status = $s WHERE id = $id AND deleted_utc IS NULL AND kind = 'upload'", ("$s", approve ? "approved" : "rejected"), ("$id", imageId)) == 0)
                throw new CommunityError(404, "not_found", "Image not found.");
            ModLog(c, admin, approve ? "approve-image" : "reject-image", imageId, null);
        }
        if (!approve) await PurgeImagesAsync("id = $id", ("$id", imageId)); // rejected files are deleted right away
        if (postId is not null) await RegeneratePreviewAsync(postId); // the crosshair preview may use this image
    }

    public void SetPostStatus(AuthUser admin, string postId, string status, string? note)
    {
        RequireAdmin(admin);
        if (status is not ("visible" or "removed")) throw new CommunityError(400, "invalid", "Unknown status.");
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        if (Database.Exec(c, "UPDATE posts SET status = $s, removed_utc = $r WHERE id = $id", ("$s", status), ("$r", status == "removed" ? Database.Utc(Now) : null), ("$id", postId)) == 0)
            throw new CommunityError(404, "not_found", "Post not found.");
        Database.Exec(c, "UPDATE reports SET status = $s WHERE post_id = $p AND status = 'open'", ("$s", status == "removed" ? "resolved" : "dismissed"), ("$p", postId));
        ModLog(c, admin, status == "removed" ? "remove-post" : "restore-post", postId, PostRules.Clean(note, 300));
        tx.Commit();
    }

    public void DismissReport(AuthUser admin, string reportId)
    {
        RequireAdmin(admin);
        using var c = db.Open();
        if (Database.Exec(c, "UPDATE reports SET status = 'dismissed' WHERE id = $id AND status = 'open'", ("$id", reportId)) == 0)
            throw new CommunityError(404, "not_found", "Report not found.");
        ModLog(c, admin, "dismiss-report", reportId, null);
    }

    public void SetUserFlags(AuthUser admin, string userId, bool? banned, bool? trusted, string? reason)
    {
        RequireAdmin(admin);
        if (userId == admin.Id && banned == true) throw new CommunityError(400, "invalid", "You can't suspend yourself.");
        using var c = db.Open();
        if (banned is { } b && Database.Exec(c, "UPDATE users SET is_banned = $b, ban_reason = $r WHERE id = $id", ("$b", b ? 1 : 0), ("$r", b ? PostRules.Clean(reason, 300) : null), ("$id", userId)) == 0)
            throw new CommunityError(404, "not_found", "User not found.");
        if (trusted is { } t && Database.Exec(c, "UPDATE users SET is_trusted = $t WHERE id = $id", ("$t", t ? 1 : 0), ("$id", userId)) == 0)
            throw new CommunityError(404, "not_found", "User not found.");
        if (banned == true) Database.Exec(c, "DELETE FROM sessions WHERE user_id = $id", ("$id", userId));
        ModLog(c, admin, banned is { } bb ? (bb ? "ban" : "unban") : trusted == true ? "trust" : "untrust", userId, PostRules.Clean(reason, 300));
    }

    // ---- cleanup ------------------------------------------------------------------------------------------------------

    /// <summary>Deletes unused uploads, images of long-removed posts, expired sessions and stale reservations.</summary>
    public async Task<int> CleanupAsync()
    {
        int n = 0;
        n += await PurgeImagesAsync("post_id IS NULL AND kind = 'upload' AND created_utc < $t", ("$t", Database.Utc(Now.AddHours(-o.OrphanImageHours))));
        n += await PurgeImagesAsync("post_id IN (SELECT id FROM posts WHERE status = 'removed' AND removed_utc < $t)", ("$t", Database.Utc(Now.AddDays(-o.RemovedRetentionDays))));
        n += await PurgeImagesAsync("post_id IS NOT NULL AND post_id NOT IN (SELECT id FROM posts)");
        using var c = db.Open();
        Database.Exec(c, "DELETE FROM sessions WHERE expires_utc < $now", ("$now", Database.Utc(Now)));
        Database.Exec(c, "DELETE FROM upload_slots WHERE state = 'reserved' AND reserved_utc < $t", ("$t", Database.Utc(Now.AddMinutes(-o.ReservationMinutes))));
        Database.Exec(c, "DELETE FROM upload_slots WHERE day < $d", ("$d", QuotaService.DayOf(Now.AddDays(-3))));
        Database.Exec(c, "DELETE FROM activity WHERE created_utc < $t", ("$t", Database.Utc(Now.AddDays(-3))));
        return n;
    }
}

public sealed class CleanupWorker(CommunityService svc, ILogger<CleanupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { var n = await svc.CleanupAsync(); if (n > 0) log.LogInformation("Cleanup removed {N} image(s)", n); }
            catch (Exception ex) { log.LogError(ex, "Cleanup failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
