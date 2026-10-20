using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MoneyTweaks.Community;
using MoneyTweaks.Community.Server;

var app = CommunityApp.Build(args);
app.Run();

/// <summary>Builds the Community Hub API. Also used by the tests, which start it on a random local port.</summary>
public static class CommunityApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var o = builder.Configuration.GetSection("Community").Get<CommunityOptions>() ?? new CommunityOptions();
        CheckConfiguration(o, builder.Environment);

        builder.Services.AddSingleton(o);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => new Database(Path.GetFullPath(o.DataDirectory)));
        builder.Services.AddHttpClient("github", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MoneyTweaks-Community/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddHttpClient("s3", c => c.Timeout = TimeSpan.FromSeconds(60));
        builder.Services.AddSingleton<IObjectStore>(sp => o.Storage == "s3"
            ? new S3ObjectStore(sp.GetRequiredService<IHttpClientFactory>().CreateClient("s3"), o.S3, sp.GetRequiredService<TimeProvider>())
            : new FileSystemObjectStore(Path.Combine(Path.GetFullPath(o.DataDirectory), "objects")));
        builder.Services.AddSingleton<ImagePipeline>();
        builder.Services.AddSingleton<QuotaService>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<CommunityService>();
        builder.Services.AddHostedService<CleanupWorker>();
        builder.Services.ConfigureHttpJsonOptions(j => { j.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; j.SerializerOptions.MaxDepth = 32; });
        builder.Services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = 429;
            r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = o.RequestsPerMinutePerIp, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 256 * 1024; // uploads raise this per request, up to MaxUploadBytes
        });

        var app = builder.Build();
        app.UseRateLimiter();
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h.XFrameOptions = "DENY";
            h.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; frame-ancestors 'none'";
            if (!app.Environment.IsDevelopment()) h.StrictTransportSecurity = "max-age=31536000";
            try { await next(); }
            catch (CommunityError e) { await Error(ctx, e.Status, e.Code, e.Message, e.Extra); }
            catch (AuthFailed e) { await Error(ctx, 401, "auth_failed", e.Message); }
            catch (BadHttpRequestException e) when (e.StatusCode == 413) { await Error(ctx, 413, "too_large", $"Images can be at most {o.MaxUploadBytes / 1024 / 1024} MB."); }
            catch (JsonException) { await Error(ctx, 400, "invalid_json", "The request isn't valid JSON."); }
            catch (BadHttpRequestException e) { await Error(ctx, e.StatusCode, "bad_request", "The request couldn't be read."); }
        });
        // Same API on both paths: /api/community/ (what the app uses, like the Cloudflare Worker) and /api/v1/ (earlier builds).
        Map(app, o, "/api/community", "/api/community/media/{id}/{variant}");
        Map(app, o, "/api/v1", "/media/{id}/{variant}");
        app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
        return app;
    }

    /// <summary>Refuses unsafe production configurations instead of running with them.</summary>
    public static void CheckConfiguration(CommunityOptions o, IHostEnvironment env)
    {
        if (o.DevSignIn && !env.IsDevelopment()) throw new InvalidOperationException("Community:DevSignIn is only allowed in the Development environment.");
        if (!env.IsDevelopment() && (string.IsNullOrEmpty(o.GitHubClientId) || string.IsNullOrEmpty(o.GitHubClientSecret)))
            throw new InvalidOperationException("GitHub sign-in (Community:GitHubClientId and GitHubClientSecret) must be configured outside Development.");
        if (o.Storage == "s3" && (o.S3.Endpoint.Length == 0 || o.S3.Bucket.Length == 0 || o.S3.AccessKeyId.Length == 0 || o.S3.SecretAccessKey.Length == 0))
            throw new InvalidOperationException("Community:S3 is incomplete.");
        if (o.Storage is not ("s3" or "filesystem")) throw new InvalidOperationException("Community:Storage must be \"s3\" or \"filesystem\".");
        if (o.ImageReview is not ("pre" or "post")) throw new InvalidOperationException("Community:ImageReview must be \"pre\" or \"post\".");
        if (o.DailyUploadLimit is < 0 or > 100) throw new InvalidOperationException("Community:DailyUploadLimit must be 0–100.");
    }

    private static Task Error(HttpContext ctx, int status, string code, string message, object? extra = null)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        object body = extra is QuotaInfo q ? new { error = code, message, quota = q }
            : extra is List<string> list ? new { error = code, message, problems = list }
            : new ApiError { Error = code, Message = message };
        return ctx.Response.WriteAsJsonAsync(body, Api.Json);
    }

    private static AuthUser Require(AuthService auth, HttpContext ctx) =>
        auth.Authenticate(ctx) ?? throw new CommunityError(401, "sign_in", "Sign in to do that.");

    private static async Task<T> Body<T>(HttpContext ctx) where T : class =>
        await ctx.Request.ReadFromJsonAsync<T>(Api.Json) ?? throw new CommunityError(400, "invalid", "Missing request body.");

    private static void Map(WebApplication app, CommunityOptions o, string prefix, string mediaRoute)
    {
        var api = app.MapGroup(prefix);
        api.MapGet("/health", () => Results.Ok(new { ok = true }));

        api.MapGet("/config", (IHostEnvironment env) => new ServerConfig
        {
            GitHubClientId = o.GitHubClientId, DailyUploadLimit = o.DailyUploadLimit, MaxUploadBytes = o.MaxUploadBytes,
            MaxImageDimension = o.MaxImageDimension, MinImageDimension = o.MinImageDimension, AcceptedTypes = ["image/png", "image/jpeg", "image/webp"],
            DevSignIn = o.DevSignIn && env.IsDevelopment(),
        });

        // ---- auth ----
        api.MapPost("/auth/github", async (HttpContext ctx, AuthService auth, QuotaService quota) =>
        {
            var body = await Body<Dictionary<string, string>>(ctx);
            var u = await auth.SignInGitHubAsync(body.GetValueOrDefault("accessToken") ?? "", ctx.RequestAborted);
            return Session(auth, quota, u);
        });
        if (o.DevSignIn && app.Environment.IsDevelopment())
            api.MapPost("/auth/dev", async (HttpContext ctx, AuthService auth, QuotaService quota) =>
            {
                var body = await Body<Dictionary<string, JsonElement>>(ctx);
                var login = body.TryGetValue("login", out var l) ? l.GetString() ?? "" : "";
                int age = body.TryGetValue("accountAgeDays", out var a) && a.TryGetInt32(out var d) ? d : 365;
                return Session(auth, quota, auth.SignInDev(login, age));
            });
        api.MapPost("/auth/signout", (HttpContext ctx, AuthService auth) => { auth.SignOut(ctx); return Results.NoContent(); });
        api.MapGet("/me", (HttpContext ctx, AuthService auth, QuotaService quota) => Me(Require(auth, ctx), quota));

        // ---- images ----
        api.MapPost("/images", async (HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var u = Require(auth, ctx);
            var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = o.MaxUploadBytes + 1;
            if (ctx.Request.ContentLength > o.MaxUploadBytes) throw new CommunityError(413, "too_large", $"Images can be at most {o.MaxUploadBytes / 1024 / 1024} MB.");
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > o.MaxUploadBytes) throw new CommunityError(413, "too_large", $"Images can be at most {o.MaxUploadBytes / 1024 / 1024} MB.");
            }
            return Results.Ok(await svc.UploadAsync(u, ms.ToArray(), ctx.Request.ContentType, ctx.RequestAborted));
        });
        api.MapDelete("/images/{id}", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            await svc.DeleteImageAsync(Require(auth, ctx), id);
            return Results.NoContent();
        });
        app.MapGet(mediaRoute, async (string id, string variant, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var m = await svc.MediaAsync(auth.Authenticate(ctx), id, variant, ctx.RequestAborted);
            if (m is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = m.Value.publicCache ? "public, max-age=3600" : "private, no-store";
            ctx.Response.Headers["Content-Disposition"] = "inline";
            return Results.Bytes(m.Value.data, m.Value.mime);
        });

        // ---- posts ----
        api.MapGet("/posts", (HttpContext ctx, AuthService auth, CommunityService svc, string? q, string? game, string? category, string? tag, string? sort, int? page, string? author) =>
            svc.ListPosts(auth.Authenticate(ctx), q, game, category, tag, sort, page ?? 1, author));
        api.MapGet("/me/bookmarks", (HttpContext ctx, AuthService auth, CommunityService svc, int? page) =>
            svc.ListPosts(Require(auth, ctx), null, null, null, null, "new", page ?? 1, bookmarks: true));
        api.MapGet("/me/posts", (HttpContext ctx, AuthService auth, CommunityService svc, int? page) =>
            svc.ListPosts(Require(auth, ctx), null, null, null, null, "new", page ?? 1, mine: true));
        api.MapGet("/posts/{id}", (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
            svc.GetPost(auth.Authenticate(ctx), id) is { } p ? Results.Ok(p) : Results.NotFound(new ApiError { Error = "not_found", Message = "Post not found." }));
        api.MapPost("/posts", async (HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var u = Require(auth, ctx);
            return Results.Ok(await svc.CreatePostAsync(u, await Body<PostInput>(ctx), ctx.RequestAborted));
        });
        api.MapPut("/posts/{id}", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var u = Require(auth, ctx);
            return Results.Ok(await svc.UpdatePostAsync(u, id, await Body<PostInput>(ctx), ctx.RequestAborted));
        });
        api.MapDelete("/posts/{id}", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            await svc.DeletePostAsync(Require(auth, ctx), id);
            return Results.NoContent();
        });
        api.MapPut("/posts/{id}/like", (string id, HttpContext ctx, AuthService auth, CommunityService svc) => svc.SetLike(Require(auth, ctx), id, true));
        api.MapDelete("/posts/{id}/like", (string id, HttpContext ctx, AuthService auth, CommunityService svc) => svc.SetLike(Require(auth, ctx), id, false));
        api.MapPut("/posts/{id}/bookmark", (string id, HttpContext ctx, AuthService auth, CommunityService svc) => svc.SetBookmark(Require(auth, ctx), id, true));
        api.MapDelete("/posts/{id}/bookmark", (string id, HttpContext ctx, AuthService auth, CommunityService svc) => svc.SetBookmark(Require(auth, ctx), id, false));
        api.MapPut("/posts/{id}/helpful", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var u = Require(auth, ctx);
            var b = await Body<Dictionary<string, bool?>>(ctx);
            return svc.SetHelpful(u, id, b.GetValueOrDefault("helpful"));
        });
        api.MapPost("/posts/{id}/imported", (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            svc.RecordImport(Require(auth, ctx), id);
            return Results.NoContent();
        });
        api.MapPost("/posts/{id}/report", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var u = Require(auth, ctx);
            svc.Report(u, id, await Body<ReportInput>(ctx));
            return Results.NoContent();
        });
        api.MapGet("/users/{login}", (string login, CommunityService svc) =>
            svc.Author(login) is { } a ? Results.Ok(a) : Results.NotFound(new ApiError { Error = "not_found", Message = "User not found." }));

        // ---- moderation ----
        var admin = api.MapGroup("/admin");
        admin.MapGet("/queue", (HttpContext ctx, AuthService auth, CommunityService svc) => svc.Queue(Require(auth, ctx)));
        admin.MapPost("/images/{id}/approve", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) => { await svc.ReviewImageAsync(Require(auth, ctx), id, true); return Results.NoContent(); });
        admin.MapPost("/images/{id}/reject", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) => { await svc.ReviewImageAsync(Require(auth, ctx), id, false); return Results.NoContent(); });
        admin.MapPost("/posts/{id}/remove", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var u = Require(auth, ctx);
            var b = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(Api.Json);
            svc.SetPostStatus(u, id, "removed", b?.GetValueOrDefault("note"));
            return Results.NoContent();
        });
        admin.MapPost("/posts/{id}/restore", (string id, HttpContext ctx, AuthService auth, CommunityService svc) => { svc.SetPostStatus(Require(auth, ctx), id, "visible", null); return Results.NoContent(); });
        admin.MapPost("/reports/{id}/dismiss", (string id, HttpContext ctx, AuthService auth, CommunityService svc) => { svc.DismissReport(Require(auth, ctx), id); return Results.NoContent(); });
        admin.MapPost("/users/{id}", async (string id, HttpContext ctx, AuthService auth, CommunityService svc) =>
        {
            var u = Require(auth, ctx);
            var b = await Body<Dictionary<string, JsonElement>>(ctx);
            bool? Flag(string k) => b.TryGetValue(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            svc.SetUserFlags(u, id, Flag("banned"), Flag("trusted"), b.TryGetValue("reason", out var r) ? r.GetString() : null);
            return Results.NoContent();
        });
    }

    private static SessionInfo Session(AuthService auth, QuotaService quota, AuthUser u)
    {
        if (u.Banned) throw new CommunityError(403, "banned", "This account is suspended from the Community Hub.");
        var (token, expires) = auth.CreateSession(u);
        return new SessionInfo { Token = token, ExpiresUtc = expires, Me = Me(u, quota) };
    }

    private static MeInfo Me(AuthUser u, QuotaService quota) => new()
    {
        User = new AuthorInfo { Id = u.Id, Login = u.Login, AvatarUrl = u.AvatarUrl, JoinedUtc = u.CreatedUtc },
        IsAdmin = u.IsAdmin, Banned = u.Banned, Quota = quota.Info(u.Id),
    };
}
