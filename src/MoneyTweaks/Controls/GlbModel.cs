using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Controls;

/// <summary>
/// The bundled third-party GPU models (Assets/Gpu, embedded): the manifest, and each file only after its SHA-256 matches
/// the manifest, so what is drawn is exactly the verified, licensed file.
/// </summary>
public static class GpuAssetCatalog
{
    private static readonly Lazy<GpuAssetManifest> Lazy = new(() =>
    {
        using var s = typeof(GpuAssetCatalog).Assembly.GetManifestResourceStream("MoneyTweaks.Gpu.manifest.json");
        return s is null ? new("", [], []) : GpuAssetManifest.Parse(new StreamReader(s).ReadToEnd());
    });
    public static GpuAssetManifest Manifest => Lazy.Value;

    /// <summary>The model file's bytes if it's bundled and its checksum matches the manifest; otherwise null.</summary>
    public static byte[]? Read(GpuAsset a)
    {
        using var s = typeof(GpuAssetCatalog).Assembly.GetManifestResourceStream("MoneyTweaks.Gpu." + a.File);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase)) { Log.Warn($"GPU model {a.File}: checksum differs from the manifest; not used"); return null; }
        return bytes;
    }
}

/// <summary>
/// Minimal glTF 2.0 binary (GLB) reader for WPF 3D: node hierarchy (TRS or matrix), triangle primitives with positions,
/// normals, texture coordinates and 8/16/32-bit indices, and PBR base colour (embedded PNG/JPEG base-colour texture, or
/// the colour factor converted linear → sRGB; metal/roughness approximated with a specular layer). Nodes named "fan" are
/// returned so they can spin around their own centre.
/// </summary>
public static class GlbModel
{
    public sealed record Result(Model3DGroup Model, Rect3D Bounds, List<AxisAngleRotation3D> Fans, int Triangles);

    public static Result Load(byte[] glb)
    {
        if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67) throw new InvalidDataException("Not a GLB file.");
        int jsonLen = BitConverter.ToInt32(glb, 12);
        using var doc = JsonDocument.Parse(glb.AsMemory(20, jsonLen));
        var j = doc.RootElement;
        int binStart = 20 + jsonLen + 8;
        var bin = glb.AsSpan(binStart).ToArray();

        var group = new Model3DGroup();
        var fans = new List<AxisAngleRotation3D>();
        var bounds = Rect3D.Empty;
        int tris = 0;
        var images = new Dictionary<int, ImageSource?>();
        var materials = j.TryGetProperty("materials", out var mats) ? mats.EnumerateArray().Select(m => MaterialOf(m, j, bin, images)).ToList() : [];

        void Visit(int nodeIdx, Matrix3D parent)
        {
            var n = j.GetProperty("nodes")[nodeIdx];
            var world = Local(n) * parent;
            string name = n.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            if (n.TryGetProperty("mesh", out var mi))
            {
                var parts = new List<GeometryModel3D>();
                foreach (var p in j.GetProperty("meshes")[mi.GetInt32()].GetProperty("primitives").EnumerateArray())
                {
                    if (p.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue;   // triangles only
                    var mesh = new MeshGeometry3D();
                    var attrs = p.GetProperty("attributes");
                    foreach (var v in Floats(j, bin, attrs.GetProperty("POSITION").GetInt32(), 3)) mesh.Positions.Add(world.Transform(new Point3D(v[0], v[1], v[2])));
                    if (attrs.TryGetProperty("TEXCOORD_0", out var ta))
                        foreach (var v in Floats(j, bin, ta.GetInt32(), 2)) mesh.TextureCoordinates.Add(new System.Windows.Point(v[0], v[1]));
                    if (attrs.TryGetProperty("NORMAL", out var na))
                        foreach (var v in Floats(j, bin, na.GetInt32(), 3)) { var nv = world.Transform(new Vector3D(v[0], v[1], v[2])); nv.Normalize(); mesh.Normals.Add(nv); }
                    if (p.TryGetProperty("indices", out var ia)) foreach (var i in Indices(j, bin, ia.GetInt32())) mesh.TriangleIndices.Add(i);
                    else for (int i = 0; i < mesh.Positions.Count; i++) mesh.TriangleIndices.Add(i);
                    if (world.Determinant < 0) for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3) (mesh.TriangleIndices[i + 1], mesh.TriangleIndices[i + 2]) = (mesh.TriangleIndices[i + 2], mesh.TriangleIndices[i + 1]);
                    mesh.Freeze();
                    tris += mesh.TriangleIndices.Count / 3;
                    var mat = p.TryGetProperty("material", out var mx) && mx.GetInt32() < materials.Count ? materials[mx.GetInt32()] : MaterialOf(default, j, bin, images);
                    var gm = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
                    parts.Add(gm);
                    bounds.Union(mesh.Bounds);
                }
                if (name.Contains("fan", StringComparison.OrdinalIgnoreCase) && parts.Count > 0)
                {
                    // Spin around the part's own centre, about its thinnest axis.
                    var b = parts.Select(x => x.Geometry.Bounds).Aggregate(Rect3D.Union);
                    var axis = b.SizeX <= b.SizeY && b.SizeX <= b.SizeZ ? new Vector3D(1, 0, 0) : b.SizeY <= b.SizeZ ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
                    var spin = new AxisAngleRotation3D(axis, fans.Count * 33);
                    fans.Add(spin);
                    var fg = new Model3DGroup { Transform = new RotateTransform3D(spin, new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2)) };
                    foreach (var x in parts) fg.Children.Add(x);
                    group.Children.Add(fg);
                }
                else foreach (var x in parts) group.Children.Add(x);
            }
            if (n.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) Visit(c.GetInt32(), world);
        }

        int scene = j.TryGetProperty("scene", out var sc) ? sc.GetInt32() : 0;
        if (j.TryGetProperty("scenes", out var scenes)) foreach (var r in scenes[scene].GetProperty("nodes").EnumerateArray()) Visit(r.GetInt32(), Matrix3D.Identity);
        else for (int i = 0; i < j.GetProperty("nodes").GetArrayLength(); i++) Visit(i, Matrix3D.Identity);
        return new Result(group, bounds, fans, tris);
    }

    private static Matrix3D Local(JsonElement n)
    {
        if (n.TryGetProperty("matrix", out var mx))
        {
            var a = mx.EnumerateArray().Select(e => e.GetDouble()).ToArray();   // column-major; WPF uses row vectors, so it reads straight across
            return new Matrix3D(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
        }
        var m = Matrix3D.Identity;
        if (n.TryGetProperty("scale", out var s)) { var v = s.EnumerateArray().Select(e => e.GetDouble()).ToArray(); m.Scale(new Vector3D(v[0], v[1], v[2])); }
        if (n.TryGetProperty("rotation", out var r)) { var q = r.EnumerateArray().Select(e => e.GetDouble()).ToArray(); m.Rotate(new Quaternion(q[0], q[1], q[2], q[3])); }
        if (n.TryGetProperty("translation", out var t)) { var v = t.EnumerateArray().Select(e => e.GetDouble()).ToArray(); m.Translate(new Vector3D(v[0], v[1], v[2])); }
        return m;
    }

    private static (int offset, int stride, int count, int comp) View(JsonElement j, int accessor, int size)
    {
        var a = j.GetProperty("accessors")[accessor];
        var bv = j.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        int offset = (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        int stride = bv.TryGetProperty("byteStride", out var st) ? st.GetInt32() : size;
        return (offset, stride, a.GetProperty("count").GetInt32(), a.GetProperty("componentType").GetInt32());
    }

    private static IEnumerable<float[]> Floats(JsonElement j, byte[] bin, int accessor, int n)
    {
        var (offset, stride, count, comp) = View(j, accessor, n * 4);
        if (comp != 5126) throw new InvalidDataException("Only float vertex data is supported.");
        for (int i = 0; i < count; i++)
        {
            var v = new float[n];
            for (int k = 0; k < n; k++) v[k] = BitConverter.ToSingle(bin, offset + i * stride + k * 4);
            yield return v;
        }
    }

    private static IEnumerable<int> Indices(JsonElement j, byte[] bin, int accessor)
    {
        var a = j.GetProperty("accessors")[accessor];
        int comp = a.GetProperty("componentType").GetInt32(), size = comp switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("Bad index type.") };
        var (offset, stride, count, _) = View(j, accessor, size);
        for (int i = 0; i < count; i++)
        {
            int at = offset + i * stride;
            yield return size == 1 ? bin[at] : size == 2 ? BitConverter.ToUInt16(bin, at) : (int)BitConverter.ToUInt32(bin, at);
        }
    }

    /// <summary>An embedded image (PNG/JPEG in a buffer view), decoded once and frozen; null if it can't be read.</summary>
    private static ImageSource? Image(JsonElement j, byte[] bin, int texture, Dictionary<int, ImageSource?> cache)
    {
        var tex = j.GetProperty("textures")[texture];
        if (!tex.TryGetProperty("source", out var src)) return null;
        int idx = src.GetInt32();
        if (cache.TryGetValue(idx, out var hit)) return hit;
        ImageSource? result = null;
        try
        {
            var img = j.GetProperty("images")[idx];
            if (img.TryGetProperty("bufferView", out var bvi))
            {
                var bv = j.GetProperty("bufferViews")[bvi.GetInt32()];
                int off = bv.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0, len = bv.GetProperty("byteLength").GetInt32();
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 1024;   // plenty for a card a few hundred pixels wide; keeps memory down
                bmp.StreamSource = new MemoryStream(bin, off, len, writable: false);
                bmp.EndInit();
                bmp.Freeze();
                result = bmp;
            }
        }
        catch (Exception ex) { Log.Debug("GLB texture: " + ex.Message); }
        return cache[idx] = result;
    }

    private static Material MaterialOf(JsonElement m, JsonElement j, byte[] bin, Dictionary<int, ImageSource?> images)
    {
        double[] c = [0.6, 0.6, 0.6, 1]; double metal = 0, rough = 1;
        ImageSource? texture = null;
        if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            if (pbr.TryGetProperty("baseColorTexture", out var bt)) texture = Image(j, bin, bt.GetProperty("index").GetInt32(), images);
            if (pbr.TryGetProperty("baseColorFactor", out var f)) c = f.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            if (pbr.TryGetProperty("metallicFactor", out var mf)) metal = mf.GetDouble();
            if (pbr.TryGetProperty("roughnessFactor", out var rf)) rough = rf.GetDouble();
        }
        static byte S(double lin) => (byte)Math.Round(Math.Clamp(Math.Pow(Math.Clamp(lin, 0, 1), 1 / 2.2), 0, 1) * 255);   // linear → sRGB
        var g = new MaterialGroup();
        // A texture is drawn as is (WPF can't multiply it by the colour factor, which is usually white with a texture).
        Brush diffuse = texture is not null
            ? new ImageBrush(texture) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new System.Windows.Rect(0, 0, 1, 1), TileMode = TileMode.Tile }
            : new SolidColorBrush(Color.FromArgb((byte)(c[3] * 255), S(c[0]), S(c[1]), S(c[2])));
        g.Children.Add(new DiffuseMaterial(diffuse));
        double spec = Math.Clamp((1 - rough) * 0.6 + metal * 0.3, 0.15, 0.85);
        g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb((byte)(spec * 255), 255, 255, 255)), 20 + 60 * (1 - rough)));
        g.Freeze();
        return g;
    }
}
