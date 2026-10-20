using MoneyTweaks.Core.Crosshair;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MoneyTweaks.Community.Server;

public sealed class ImageRejected(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>A processed image: re-encoded full size and thumbnail, metadata removed.</summary>
public sealed record ProcessedImage(byte[] Full, string FullMime, byte[] Thumb, string ThumbMime, int Width, int Height, bool HasAlpha)
{
    public string Ext => FullMime == "image/png" ? "png" : "jpg";
    public string ThumbExt => ThumbMime == "image/png" ? "png" : "jpg";
}

/// <summary>
/// Turns untrusted bytes into a safe image. Order: size limit → file signature (PNG, JPEG or WebP only; SVG, GIF,
/// executables, archives and everything else rejected) → header-only identify (dimensions, frame count) → decode with only
/// those three decoders (pure managed code) → re-encode from pixels. Re-encoding drops every byte of the original file:
/// EXIF/GPS, comments, ICC/XMP profiles, trailing data and anything hidden in it. PNGs and other images with real
/// transparency stay PNG (alpha preserved, for crosshairs); everything else becomes JPEG. WebP is accepted as input and
/// converted, because Windows can't always display WebP.
/// </summary>
public sealed class ImagePipeline(CommunityOptions o)
{
    private static readonly Configuration Decoders = new(new PngConfigurationModule(), new JpegConfigurationModule(), new WebpConfigurationModule());
    private readonly SemaphoreSlim _cpu = new(Math.Max(1, Environment.ProcessorCount / 2));

    public static string? Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public async Task<ProcessedImage> ProcessAsync(byte[] data, string? declaredType, CancellationToken ct)
    {
        if (data.Length == 0) throw new ImageRejected(400, "empty", "The file is empty.");
        if (data.Length > o.MaxUploadBytes) throw new ImageRejected(413, "too_large", $"Images can be at most {o.MaxUploadBytes / 1024 / 1024} MB.");
        var sniffed = Sniff(data) ?? throw new ImageRejected(415, "unsupported_type", "Only PNG, JPEG and WebP images are accepted.");
        var declared = declaredType?.Split(';')[0].Trim().ToLowerInvariant();
        if (declared is not null && declared != "application/octet-stream" && declared != sniffed && !(declared == "image/jpg" && sniffed == "image/jpeg"))
            throw new ImageRejected(415, "type_mismatch", "The file's contents don't match its type.");

        await _cpu.WaitAsync(ct);
        try { return await Task.Run(() => Process(data), ct); }
        finally { _cpu.Release(); }
    }

    private ProcessedImage Process(byte[] data)
    {
        // SkipMetadata: EXIF, ICC, XMP and text chunks are never parsed (less attack surface; they'd be dropped anyway).
        var options = new DecoderOptions { Configuration = Decoders, MaxFrames = 1, SkipMetadata = true };
        SixLabors.ImageSharp.ImageInfo info;
        try { info = Image.Identify(options, data); }
        catch (Exception) { throw new ImageRejected(422, "unreadable", "The image couldn't be read. It may be damaged."); }
        if (info.Width < o.MinImageDimension || info.Height < o.MinImageDimension)
            throw new ImageRejected(422, "too_small", $"Images must be at least {o.MinImageDimension}×{o.MinImageDimension} pixels.");
        if (info.Width > o.MaxImageDimension || info.Height > o.MaxImageDimension || (long)info.Width * info.Height > o.MaxImagePixels)
            throw new ImageRejected(422, "too_big", $"Images can be at most {o.MaxImageDimension}×{o.MaxImageDimension} pixels.");
        if (info.FrameMetadataCollection.Count > 1) throw new ImageRejected(422, "animated", "Animated images aren't supported.");

        Image<Rgba32> img;
        try { img = Image.Load<Rgba32>(options, data); }
        catch (Exception) { throw new ImageRejected(422, "unreadable", "The image couldn't be read. It may be damaged."); }
        using (img)
        {
            bool alpha = HasAlpha(img);
            var full = Encode(img, alpha, o.JpegQuality);
            using var thumb = img.Clone(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(o.ThumbnailSize, o.ThumbnailSize), Sampler = KnownResamplers.Lanczos3 }));
            var th = Encode(thumb, alpha, 80);
            return new ProcessedImage(full, alpha ? "image/png" : "image/jpeg", th, alpha ? "image/png" : "image/jpeg", img.Width, img.Height, alpha);
        }
    }

    public static bool HasAlpha(Image<Rgba32> img)
    {
        bool any = false;
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height && !any; y++)
                foreach (var p in rows.GetRowSpan(y)) if (p.A < 255) { any = true; break; }
        });
        return any;
    }

    /// <summary>Encodes pixels only: every metadata profile is cleared first.</summary>
    public static byte[] Encode(Image<Rgba32> img, bool alpha, int quality)
    {
        img.Metadata.ExifProfile = null;
        img.Metadata.IptcProfile = null;
        img.Metadata.XmpProfile = null;
        img.Metadata.IccProfile = null;
        img.Metadata.CicpProfile = null;
        var png = img.Metadata.GetPngMetadata();
        png.TextData.Clear();
        using var ms = new MemoryStream();
        if (alpha) img.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8, CompressionLevel = PngCompressionLevel.BestCompression, ChunkFilter = PngChunkFilter.ExcludeAll });
        else img.SaveAsJpeg(ms, new JpegEncoder { Quality = quality, SkipMetadata = true });
        return ms.ToArray();
    }
}

/// <summary>
/// Renders a crosshair profile to a preview PNG with the app's own geometry (same shapes, same pixel snapping), drawn
/// pixel-exact and then enlarged without smoothing, on the app's dark background. Generated by the server, so it's
/// trustworthy and doesn't use the user's upload allowance. Image layers are drawn only from approved images.
/// </summary>
public static class CrosshairPreview
{
    public const int Size = 256;
    private static readonly Rgba32 Background = new(0x0B, 0x0E, 0x14, 0xFF);

    public static byte[] Render(CrosshairProfile p, Func<string, Image<Rgba32>?> imageFor)
    {
        double extent = CrosshairGeometry.Extent(p, 1);
        int half = (int)Math.Ceiling(extent) + 3;
        int n = Math.Clamp(half * 2 + 1, 16, Size);
        double scale = half * 2 + 1 > Size ? (double)(Size / 2 - 3) / Math.Ceiling(extent) : 1;
        var drawings = CrosshairGeometry.Build(p, 0, scale);
        int c = n / 2;
        var px = new Rgba32[n * n];
        Array.Fill(px, Background);
        const int ss = 4; // 4×4 samples per pixel for smooth circles and rotated lines
        Span<double> acc = stackalloc double[4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                acc.Clear();
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double gx = x - c + (sx + 0.5) / ss, gy = y - c + (sy + 0.5) / ss;
                        var (r, g, b) = (Background.R / 255.0, Background.G / 255.0, Background.B / 255.0);
                        foreach (var d in drawings)
                        {
                            double lx = gx, ly = gy;
                            if (Math.Abs(d.Rotation % 360) > 0.001)
                            {
                                double a = -d.Rotation * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
                                double dx = gx - d.PivotX, dy = gy - d.PivotY;
                                lx = d.PivotX + dx * cos - dy * sin;
                                ly = d.PivotY + dx * sin + dy * cos;
                            }
                            foreach (var s in d.Shapes)
                            {
                                var (cr, cg, cb, ca) = Sample(s, lx, ly, imageFor);
                                if (ca <= 0) continue;
                                r = cr * ca + r * (1 - ca); g = cg * ca + g * (1 - ca); b = cb * ca + b * (1 - ca);
                            }
                        }
                        acc[0] += r; acc[1] += g; acc[2] += b;
                    }
                px[y * n + x] = new Rgba32((byte)Math.Round(acc[0] / (ss * ss) * 255), (byte)Math.Round(acc[1] / (ss * ss) * 255), (byte)Math.Round(acc[2] / (ss * ss) * 255), 255);
            }
        using var small = Image.LoadPixelData<Rgba32>(px, n, n);
        int zoom = Math.Max(1, Size / n);
        using var canvas = new Image<Rgba32>(Size, Size, Background);
        using var big = small.Clone(i => i.Resize(n * zoom, n * zoom, KnownResamplers.NearestNeighbor));
        canvas.Mutate(i => i.DrawImage(big, new Point((Size - n * zoom) / 2, (Size - n * zoom) / 2), 1f));
        return EncodePng(canvas);
    }

    private static byte[] EncodePng(Image<Rgba32> img)
    {
        using var ms = new MemoryStream();
        img.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.Rgb, CompressionLevel = PngCompressionLevel.BestCompression, ChunkFilter = PngChunkFilter.ExcludeAll });
        return ms.ToArray();
    }

    private static (double r, double g, double b, double a) Sample(CrosshairShape s, double x, double y, Func<string, Image<Rgba32>?> imageFor)
    {
        double a = s.Color.A / 255.0 * Math.Clamp(s.Opacity, 0, 1);
        (double, double, double, double) C(double alpha) => (s.Color.R / 255.0, s.Color.G / 255.0, s.Color.B / 255.0, alpha);
        switch (s.Kind)
        {
            case ShapeKind.Rect:
                if (x < s.X || x >= s.X + s.W || y < s.Y || y >= s.Y + s.H) return default;
                if (s.CornerRadius > 0)
                {
                    double r = Math.Min(s.CornerRadius, Math.Min(s.W, s.H) / 2);
                    double qx = Math.Max(Math.Max(s.X + r - x, x - (s.X + s.W - r)), 0), qy = Math.Max(Math.Max(s.Y + r - y, y - (s.Y + s.H - r)), 0);
                    if (qx * qx + qy * qy > r * r) return default;
                }
                return C(a);
            case ShapeKind.Ellipse:
            {
                double rx = s.W / 2, ry = s.H / 2, dx = (x - s.X - rx) / rx, dy = (y - s.Y - ry) / ry;
                return dx * dx + dy * dy <= 1 ? C(a) : default;
            }
            case ShapeKind.Ring:
            {
                double r = s.W / 2, d = Math.Sqrt(Math.Pow(x - s.X - r, 2) + Math.Pow(y - s.Y - r, 2));
                return Math.Abs(d - r) <= s.StrokeThickness / 2 ? C(a) : default;
            }
            case ShapeKind.Image when s.ImageFile is not null && imageFor(s.ImageFile) is { } img:
            {
                if (x < s.X || x >= s.X + s.W || y < s.Y || y >= s.Y + s.H) return default;
                int ix = Math.Clamp((int)((x - s.X) / s.W * img.Width), 0, img.Width - 1), iy = Math.Clamp((int)((y - s.Y) / s.H * img.Height), 0, img.Height - 1);
                var p = img[ix, iy];
                return (p.R / 255.0, p.G / 255.0, p.B / 255.0, p.A / 255.0 * Math.Clamp(s.Opacity, 0, 1));
            }
            default:
                return default;
        }
    }
}
