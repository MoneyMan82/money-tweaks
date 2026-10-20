namespace MoneyTweaks.Community.Server;

/// <summary>Configuration (appsettings.json section "Community" or environment variables Community__X). All limits live here.</summary>
public sealed class CommunityOptions
{
    /// <summary>Folder for the SQLite database (and images when Storage = "filesystem"). Must be a persistent volume.</summary>
    public string DataDirectory { get; set; } = "data";

    // ---- identity ----
    public string? GitHubClientId { get; set; }
    public string? GitHubClientSecret { get; set; }
    /// <summary>GitHub user ids (numbers, not logins: logins can change) that get moderator rights.</summary>
    public List<long> AdminGitHubIds { get; set; } = [];
    /// <summary>Development-only sign-in without GitHub, for local testing. Refused outside the Development environment.</summary>
    public bool DevSignIn { get; set; }
    public int SessionDays { get; set; } = 30;
    /// <summary>New GitHub accounts can't post or upload (a simple, effective spam barrier).</summary>
    public int MinAccountAgeDays { get; set; } = 7;

    // ---- uploads ----
    /// <summary>Successful image uploads per account per UTC calendar day.</summary>
    public int DailyUploadLimit { get; set; } = 2;
    public long MaxUploadBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxImageDimension { get; set; } = 4096;
    public int MinImageDimension { get; set; } = 16;
    public long MaxImagePixels { get; set; } = 16_777_216;
    public int ThumbnailSize { get; set; } = 480;
    public int JpegQuality { get; set; } = 88;
    /// <summary>"pre": uploads stay hidden until a moderator approves them (trusted users skip this). "post": visible at once.</summary>
    public string ImageReview { get; set; } = "pre";
    /// <summary>Upload refused when the whole store would exceed this (protects the storage bill).</summary>
    public long MaxTotalStorageBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    /// <summary>Uploads never attached to a post are deleted after this many hours (they still counted toward the limit).</summary>
    public int OrphanImageHours { get; set; } = 24;
    /// <summary>Images of posts removed by moderators are kept this long for review, then deleted.</summary>
    public int RemovedRetentionDays { get; set; } = 30;
    /// <summary>A reservation whose upload never finished (crash) stops counting after this many minutes.</summary>
    public int ReservationMinutes { get; set; } = 10;

    // ---- storage ----
    /// <summary>"filesystem" (DataDirectory/objects, for local or single-server hosting with a persistent disk) or "s3".</summary>
    public string Storage { get; set; } = "filesystem";
    public S3Options S3 { get; set; } = new();

    // ---- spam and moderation ----
    public int PostsPerDay { get; set; } = 5;
    public int ReportsPerDay { get; set; } = 20;
    /// <summary>Distinct open reports that hide a post until a moderator reviews it.</summary>
    public int ReportHideThreshold { get; set; } = 3;
    /// <summary>Words or phrases that block publishing (case-insensitive, whole words). Moderators maintain it.</summary>
    public List<string> BlockedTerms { get; set; } = [];
    public int RequestsPerMinutePerIp { get; set; } = 240;
}

/// <summary>S3-compatible object storage (Cloudflare R2, AWS S3, Backblaze B2, MinIO…). Credentials come from configuration/secrets, never the repo.</summary>
public sealed class S3Options
{
    /// <summary>e.g. https://&lt;account&gt;.r2.cloudflarestorage.com</summary>
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "auto";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
}
