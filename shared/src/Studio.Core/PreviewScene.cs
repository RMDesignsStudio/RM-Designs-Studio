using System.Numerics;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;

namespace Studio.Core;

/// <summary>
/// The data behind <see cref="WheelPreview"/>: geometry in car space (every vertex placed by its bone), the materials
/// the car's material binding gives each slot (shader kind, textures, parameters) and the textures themselves, kept in
/// their GPU block format (BC1/3/4/5/7) so the browser uploads them as they are; all binary data is zlib + base64.
/// Textures and materials are shared between cars and between before / after.
/// </summary>
public sealed class PreviewScene(GameData game)
{
    // material kinds understood by the page's shader
    public const int KGeneric = 0, KPaint = 1, KCarbon = 2, KGlass = 3, KScreen = 4, KMirror = 5, KDecal = 6, KTyre = 7, KLight = 8, KMultiPaint = 10;

    /// <summary>The page's textures; complete after <see cref="Finish"/>.</summary>
    public List<Dictionary<string, object?>> Textures { get; } = [];
    readonly List<(ErpGfxSRVResource Srv, string Name, int Max, Func<byte[], byte[]>? Bake)> _pending = [];
    public List<Dictionary<string, object?>> Materials { get; } = [];
    /// <summary>Vertex / index data, shared by every group (and car) with identical geometry.</summary>
    public List<Dictionary<string, object>> Geometry { get; } = [];
    readonly Dictionary<string, int> _geo = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _tex = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _mat = new(StringComparer.Ordinal);
    readonly Dictionary<int, ErpFile?> _common = [];

    /// <summary>Largest texture side for paint and decal sheets / for everything else.</summary>
    public int PaintSize = 2048, TexSize = 1024;

    /// <summary>Textures (SRV ids) a host may replace live: each gets a page texture of its own (others are shared by
    /// content), at least <see cref="PaintSize"/>, found through <see cref="TextureOf"/>.</summary>
    public HashSet<string> Editable { get; } = new(StringComparer.Ordinal);
    /// <summary>Page texture index of each <see cref="Editable"/> texture a material uses.</summary>
    public Dictionary<string, int> TextureOf { get; } = new(StringComparer.Ordinal);

    /// <summary>The shared vehicle resources (generic materials, glass, carbon patterns, tyres): teams/common.erp.</summary>
    public ErpFile? Common(int season)
    {
        if (_common.TryGetValue(season, out var c)) return c;
        var p = Path.Combine(game.Assets, $"f1_{season}_vehicle_package", "teams", "common.erp");
        try { c = File.Exists(p) ? ErpX.Load(p) : null; } catch (InvalidDataException) { c = null; }
        return _common[season] = c;
    }

    /// <summary>Resources looked up through layers, top first.</summary>
    public sealed class Lookup(IEnumerable<IReadOnlyDictionary<string, ErpResource>> layers)
    {
        readonly List<IReadOnlyDictionary<string, ErpResource>> _layers = layers.ToList();
        public ErpResource? Find(string id)
        {
            foreach (var l in _layers) if (l.TryGetValue(id, out var r)) return r;
            return null;
        }
    }

    /// <summary>Slot name -> material eaid, from the car's material binding(s) (first binding wins).</summary>
    public static Dictionary<string, string> Binding(Lookup lk, IEnumerable<string> embIds)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in embIds)
            if (lk.Find(id) is { } r)
                foreach (var (slot, mat) in EgoMaterial.ParseBinding(ErpX.Data(r))) map.TryAdd(slot, mat);
        return map;
    }

    public static int KindOf(string shader) => shader switch
    {
        _ when shader.Contains("shadow") || shader.Contains("rain_beads") => -1,
        _ when shader.StartsWith("vehicle_carbon") => KCarbon,
        _ when shader.StartsWith("vehicle_floating_decal") => KDecal,   // incl. vehicle_floating_decal_surface_map (e.g. VCARB numbers)
        _ when shader.StartsWith("char_logo") => KDecal,                  // a suit's logos and badges (their own geometry)
        _ when shader.StartsWith("char_hair") => -1,                      // glove frays, hair cards (alpha dither): not drawn
        _ when shader.Contains("paint") => KPaint,
        "vehicle_glass" => KGlass,
        "vehicle_lcd_screen" => KScreen,
        _ when shader.Contains("mirror") => KMirror,
        _ when shader.StartsWith("tyre_") => KTyre,
        _ when shader.Contains("revs") || shader.Contains("indicator") || shader.Contains("rear_light") => KLight,
        _ => KGeneric,
    };

    /// <summary>The page material for a slot, or -1 when the slot is not drawn (no material, shadow, rain).</summary>
    public int Material(Lookup lk, Dictionary<string, string> binding, string slot)
    {
        if (!binding.TryGetValue(slot, out var mid) || lk.Find(mid) is not { } mr) return -1;
        EgoMaterial m;
        try { m = EgoMaterial.Parse(ErpX.Data(mr)); }
        catch (InvalidDataException) { return -1; }
        int kind = KindOf(m.ShaderName);
        if (kind < 0) return -1;

        // (character materials name their samplers differently: me_baseDiffuseTex, me_logoDiffuseTex, me_diffuseTex, ...)
        string[] d = kind switch { KPaint or KDecal => ["me_albedoMap", "me_logoDiffuseTex"], _ => ["me_diffuseMap", "me_baseDiffuseTex", "me_diffuseTex"] };
        bool character = m.ShaderName.StartsWith("char_") || m.ShaderName.StartsWith("skin_");
        string? Sampler(params string[] names) => names.Select(n => m.Textures.GetValueOrDefault(n)).FirstOrDefault(t => t is not null);
        bool paint = kind is KPaint;
        int T(string? id, int max) => id is null ? -1 : Texture(lk, id, max);
        var tex = new Dictionary<string, int>
        {
            ["d"] = T(Sampler(d), paint || kind == KTyre ? PaintSize : TexSize),
            ["n"] = paint ? -1 : T(kind == KDecal && character ? Sampler("me_logoNormalTex") : Sampler("me_normalMap", "me_baseNormalTex", "me_normalTex"),
                                   kind == KTyre ? TexSize : Math.Max(TexSize / 2, 64)),
            ["s"] = character && kind == KDecal ? -1 : T(Sampler(paint ? "me_paintSurfaceMap" : "me_surfaceMap", "me_surfaceTex"), paint || kind == KTyre ? TexSize : Math.Max(TexSize / 2, 64)),
            // tyres: the sidewall paint mask (R = dry-tyre band + lettering, A = coverage), tinted by me_logoColour
            ["l"] = kind == KTyre ? T(Sampler("me_logoSurfaceMap"), TexSize) : T(Sampler("me_logoMap"), paint ? PaintSize : TexSize),
            // paint: the metallic areas' reflection colour (paint_c) and the flake normal map
            ["c"] = paint ? T(Sampler("me_metallicMap"), TexSize) : -1,
            ["f"] = paint && m.Value("me_flakesStrength", 0) > 0 ? T(Sampler("me_flakesMap"), 512) : -1,
            // the decals' own surface (G = roughness, B = metallic), on the decal sheet
            ["ls"] = kind == KTyre ? -1 : T(Sampler("me_logoSurfaceMap"), Math.Max(TexSize / 2, 64)),
        };
        float V(string n, float f) => m.Value(n, f);
        float[] Vec(string n, int k, float f) { var v = m.Values.GetValueOrDefault(n) ?? []; return Enumerable.Range(0, k).Select(i => i < v.Length ? v[i] : f).ToArray(); }
        // vehicle_multi_paint (e.g. Aston Martin's Monaco 26 livery): no colour map, but four colour layers placed by a mask
        // (R, G, B, A = layers 1-4), each with its colour, a "flip" colour it turns to at glancing angles (a colour-shift
        // paint: Monaco's maroon-to-gold), roughness, metallic and lacquer. The page mixes them per pixel (kind 10): the
        // mask goes over as it is (linear RGBA), the layers as ml = 4 x [albedo, roughness | flip colour, flip power | metallic flip colour, metallic | lacquer, has metallic flip]
        float[]? ml = null;
        if (paint && Sampler("me_maskMap") is { } mask && Sampler(d) is null)
        {
            ml = Enumerable.Range(1, 4).SelectMany(i =>
            {
                var flip = Vec($"me_layer{i}_flip_albedo", 3, 0);
                bool flips = flip.Any(x => x > 0);
                var flipIor = Vec($"me_layer{i}_metallicFlipIor", 3, 0);
                return (float[])[.. Vec($"me_layer{i}_albedo", 3, 0), V($"me_layer{i}_roughness", 0.35f),
                                  .. flip, flips ? V($"me_layer{i}_flip_power", 1) : 0,
                                  .. flipIor, V($"me_layer{i}_metallic", 0),
                                  V($"me_layer{i}_lacquer_mask", 0), flipIor.Any(x => x > 0) ? 1 : 0, 0, 0];
            }).ToArray();
            tex["d"] = Texture(lk, mask, PaintSize, ("multi-paint-mask|" + mid, rgba => rgba));
            tex["s"] = tex["c"] = tex["f"] = -1;
            kind = KMultiPaint;
        }
        var uvTile = Vec("me_uvTile", 2, 1);
        // the tread sheets stack 8 strips (clean rubber, gravel, grass, marbles, then worn); a new tyre is the first
        if (m.ShaderName == "tyre_tread") uvTile = [1, -1];   // strip sheets: the page picks each sheet's first strip
        float[] p =
        [
            uvTile[0], uvTile[1], V("me_uOffset", 0), V("me_vOffset", 0),                         // p0: uv tile, carbon pattern offset
            V("me_uPaintOffset", 0), V("me_vPaintOffset", 0), V("me_carbonTile", 1), V("me_lacquerRoughness", 0.05f),
            V("me_logoRoughness", 0.35f), kind == KPaint ? 1f : V("me_lacquerLevel", 0), V("me_screenSelfIllumination", 0), V("me_vertexAo", 0),
            .. Vec("me_logoColour", 3, 1), V("me_cubeMapScale", 1),                                 // p3: tyre band colour, reflection strength
            .. kind == KCarbon
                // p4 (carbon): the fibres' Ashikhmin-Shirley exponents along / across a tow and their IOR (vehicle_carbon)
                ? new[] { V("me_carbonAnisotropyX", 100), V("me_carbonAnisotropyY", 20), V("me_carbonIor", 1.5f), 0 }
                : [V("me_flakesStrength", 0), V("me_flakesTile", 1), 0, 0],                             // p4: metallic flakes
            .. Vec("me_flakesColourInfluence", 3, 0), character && kind == KDecal ? 1 : 0,          // p5: flake tint; w: a decal lying on its surface (a suit's logos)
        ];
        var key = $"{mid}|{string.Join(",", tex.Values)}";
        if (_mat.TryGetValue(key, out var idx)) return idx;
        Materials.Add(new() { ["name"] = mid.Split('/')[^1], ["k"] = kind, ["t"] = tex, ["p"] = p, ["blend"] = kind is KGlass or KDecal, ["ml"] = ml });
        return _mat[key] = Materials.Count - 1;
    }

    /// <summary>A texture for the page (index into <see cref="Textures"/>), or -1 if it cannot be read. The same
    /// texture asked for at different sizes is stored once, at the largest.</summary>
    public int Texture(Lookup lk, string srvId, int maxSize, (string Key, Func<byte[], byte[]> Fn)? bake = null)
    {
        var r = lk.Find(srvId);
        if (r is null) return -1;
        ErpResource? surface;
        var srv = new ErpGfxSRVResource();
        try { srv.FromResource(r); surface = r.ParentFile.FindResource(srv.Fragment0.SurfaceResourceName); }
        catch (Exception) { return -1; }
        // keyed by content: templates often carry the same texture several times under hashes SERPs wrote; an editable
        // texture is keyed by its id, so replacing it never changes another texture that happens to look the same
        if (Editable.Contains(srvId) && bake is null)
        {
            if (TextureOf.TryGetValue(srvId, out var own)) return own;
            _pending.Add((srv, srvId.Split('/')[^1], Math.Max(maxSize, PaintSize), null));
            return TextureOf[srvId] = _pending.Count - 1;
        }
        string key;
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            if (surface is not null) foreach (var f in surface.Fragments) { var d = f.GetDataArray(true); md5.TransformBlock(d, 0, d.Length, null, 0); }
            md5.TransformFinalBlock([], 0, 0);
            key = Convert.ToHexString(md5.Hash!) + (bake is { } b ? "|" + b.Key : "");
        }
        if (_tex.TryGetValue(key, out var idx))
        {
            _pending[idx] = _pending[idx] with { Max = Math.Max(_pending[idx].Max, maxSize) };
            return idx;
        }
        _pending.Add((srv, srvId.Split('/')[^1], maxSize, bake?.Fn));
        return _tex[key] = _pending.Count - 1;
    }

    /// <summary>Replace a page texture (after <see cref="Finish"/>), e.g. with an imported image.</summary>
    public void ReplaceTexture(int index, Dictionary<string, object?> tex)
    {
        tex["name"] = Textures[index]["name"];
        Textures[index] = tex;
    }

    /// <summary>A page texture from finished levels, largest first: block data ("bc1", "bc3", "bc7", ...) or "rgba" (RGBA8).</summary>
    public static Dictionary<string, object?> PageTexture(string format, int w, int h, IEnumerable<byte[]> levels) =>
        new() { ["f"] = format, ["w"] = w, ["h"] = h, ["l"] = levels.Select(l => Pack(l)).ToList() };

    /// <summary>Decode every texture asked for (unreadable ones become null entries the page skips).</summary>
    public void Finish()
    {
        for (int i = Textures.Count; i < _pending.Count; i++)
        {
            var (srv, name, max, bakeFn) = _pending[i];
            Dictionary<string, object?>? t = null;
            try
            {
                Stream? mips = null;
                if (srv.SurfaceRes.HasMips && srv.SurfaceRes.HasValidMips && game.MipmapsPath(srv.SurfaceRes.Frag2.MipMapFileName) is { } mp)
                    mips = File.OpenRead(mp);
                DdsFile dds;
                // a mipmaps file that doesn't match the texture (e.g. found under another season's package): the texture's
                // own smaller levels inside the ERP are used instead
                try { using (mips) dds = srv.ToDdsFile(mips, false, 0); }
                catch (Exception) when (mips is not null) { dds = srv.ToDdsFile(null, false, 0); }
                t = bakeFn is not null ? Rgba(dds, Math.Min(max, 2048), bakeFn) : Blocks(dds, max) ?? Rgba(dds, Math.Min(max, 2048));
            }
            catch (Exception) { }
            if (t is not null) t["name"] = name;
            Textures.Add(t ?? new() { ["name"] = name, ["f"] = "none", ["w"] = 0, ["h"] = 0, ["l"] = new List<string>() });
        }
    }

    /// <summary>The DDS mip chain in its block format, from the first level of at most <paramref name="maxSize"/>² pixels
    /// down to 4x4 (only the first slice of an array).</summary>
    Dictionary<string, object?>? Blocks(DdsFile dds, int maxSize)
    {
        var (fmt, bb) = FormatOf(dds);
        if (fmt is null) return null;
        int w = (int)dds.header.width, h = (int)dds.header.height, n = Math.Max(1, (int)dds.header.mipMapCount);
        var data = dds.bdata;
        var levels = new List<string>();
        int off = 0, first = -1;
        for (int i = 0; i < n; i++)
        {
            int lw = Math.Max(1, w >> i), lh = Math.Max(1, h >> i);
            int size = Math.Max(1, (lw + 3) / 4) * Math.Max(1, (lh + 3) / 4) * bb;
            if (off + size > data.Length) break;
            if (lw < 4 || lh < 4) break;
            if ((long)lw * lh <= (long)maxSize * maxSize)   // by area: tall strip sheets (tyre treads) keep their width
            {
                // WebGL only takes block-compressed levels whose sides are multiples of 4 (a 4267x4267 sheet is not):
                // such a texture is decoded instead
                if (lw % 4 != 0 || lh % 4 != 0) return null;
                if (first < 0) first = i;
                levels.Add(Blob(data.AsSpan(off, size)));
            }
            off += size;
        }
        if (first < 0) return null;
        return new() { ["f"] = fmt, ["w"] = Math.Max(1, w >> first), ["h"] = Math.Max(1, h >> first), ["l"] = levels };
    }

    static (string?, int) FormatOf(DdsFile dds)
    {
        const uint DX10 = 0x30315844;
        uint cc = dds.header.ddspf.fourCC;
        if (cc == DX10)
            return (int)dds.header10.dxgiFormat switch
            {
                >= 70 and <= 72 => ("bc1", 8), >= 73 and <= 75 => ("bc2", 16), >= 76 and <= 78 => ("bc3", 16),
                >= 79 and <= 81 => ("bc4", 8), >= 82 and <= 84 => ("bc5", 16), >= 97 and <= 99 => ("bc7", 16),
                _ => (null, 0),
            };
        return cc switch
        {
            0x31545844 => ("bc1", 8),                       // DXT1
            0x32545844 or 0x33545844 => ("bc2", 16),        // DXT2/3
            0x34545844 or 0x35545844 => ("bc3", 16),        // DXT4/5
            0x31495441 or 0x55344342 => ("bc4", 8),         // ATI1 / BC4U
            0x32495441 or 0x55354342 => ("bc5", 16),        // ATI2 / BC5U
            _ => (null, 0),
        };
    }

    /// <summary>Fallback for other formats: decoded RGBA8, one level (passed through <paramref name="bake"/> when given).</summary>
    Dictionary<string, object?>? Rgba(DdsFile dds, int maxSize, Func<byte[], byte[]>? bake = null)
    {
        using var ms = new MemoryStream();
        dds.Write(ms);
        ms.Position = 0;
        var levels = new BcDecoder().DecodeAllMipMaps2D(ms);
        if (levels.Length == 0) return null;
        var img = levels.FirstOrDefault(l => l.Width <= maxSize && l.Height <= maxSize);
        if (img.IsEmpty) img = levels[^1];
        var rgba = new byte[img.Width * img.Height * 4];
        for (int y = 0; y < img.Height; y++) MemoryMarshal.AsBytes(img.Span.GetRowSpan(y)).CopyTo(rgba.AsSpan(y * img.Width * 4));
        if (bake is not null) rgba = bake(rgba);
        return new() { ["f"] = "rgba", ["w"] = img.Width, ["h"] = img.Height, ["l"] = new List<string> { Blob(rgba) } };
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>Where a mesh's buffers come from: a car model, or plain resources (the shared tyres).</summary>
    public sealed record Buffers(Func<string, byte[]> Header, Func<string, byte[]> Data)
    {
        public static Buffers Of(CarModel car) => new(e => car.Frags(e)[0], e => car.Frags(e)[1]);
        public static Buffers Of(Lookup lk) => new(e => ErpX.Data(lk.Find(e)!), e => ErpX.Data(lk.Find(e)!, 1));
    }

    /// <summary>The shared tyre model (common.erp, shared/tyres): its four tread + wall pairs sit on the car's
    /// wheel_bl / br / fl / fr nodes.</summary>
    public IEnumerable<Dictionary<string, object>> Tyres(int season, NodeTable nt, Func<int, Matrix4x4> w,
        IReadOnlyDictionary<string, ErpResource> commonIx, float[] box)
    {
        var pre = $"eaid://f1_{season}_vehicle_package/shared/tyres/idf/tyres";
        if (!commonIx.TryGetValue(pre + ".idf?lod=0&mesh", out var mr)) return [];
        var lk = new PreviewScene.Lookup([commonIx]);
        var bind = PreviewScene.Binding(lk, [pre + ".emb?context=default"]);
        var mesh = MeshDesc.Parse(ErpX.Data(mr));
        var corners = new[] { "wheel_bl", "wheel_br", "wheel_fl", "wheel_fr" };
        var groups = new List<Dictionary<string, object>>();
        for (int i = 0; i < corners.Length; i++)
        {
            var node = nt.Nodes.FirstOrDefault(n => n.Name == corners[i]);
            if (node is null) continue;
            var at = w(node.Index);
            var subs = mesh.Subs.Skip(i * 2).Take(2).Select(s => (mesh, s));
            groups.AddRange(Groups(PreviewScene.Buffers.Of(lk), subs, s => Material(lk, bind, s.Name), _ => at, box));
        }
        return groups;
    }

    /// <summary>World matrices of a model's nodes (car space). A car whose front and rear wheel nodes sit at different heights
    /// (the 2026 Racing Bulls cars: the front ones 5 cm up; in game the suspension puts every wheel on the road) is tilted
    /// whole about its rear axle until both axles are level, so all four tyres stand on the floor; nothing comes apart.</summary>
    public static Func<int, Matrix4x4> NodeWorld(NodeTable nt)
    {
        var world = new Dictionary<int, Matrix4x4>();
        Matrix4x4 W(int n)
        {
            if (world.TryGetValue(n, out var w)) return w;
            var x = nt.Nodes[n];
            var l = Matrix4x4.CreateScale(x.S[0], x.S[1], x.S[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(x.Q[0], x.Q[1], x.Q[2], x.Q[3])) *
                    Matrix4x4.CreateTranslation(x.T[0], x.T[1], x.T[2]);
            return world[n] = x.Parent >= 0 ? l * W(x.Parent) : l;
        }
        Vector3? At(string name) => nt.Nodes.FirstOrDefault(x => x.Name == name) is { } node ? W(node.Index).Translation : null;
        if (At("wheel_fl") is { } fl && At("wheel_fr") is { } fr && At("wheel_bl") is { } bl && At("wheel_br") is { } br)
        {
            var front = (fl + fr) / 2; var rear = (bl + br) / 2;
            if (MathF.Abs(front.Y - rear.Y) > 0.005f && MathF.Abs(front.Z - rear.Z) > 0.5f)
            {
                // the turn about the x axis through the rear axle that brings the front axle down (or up) to its height
                var angle = MathF.Atan2(front.Y - rear.Y, front.Z - rear.Z);
                var level = Matrix4x4.CreateRotationX(angle, rear);
                if (MathF.Abs(Vector3.Transform(front, level).Y - rear.Y) > MathF.Abs(front.Y - rear.Y)) level = Matrix4x4.CreateRotationX(-angle, rear);
                return n => W(n) * level;
            }
        }
        return W;
    }

    /// <summary>One draw group per submesh: car-space float32 positions, half normal (w = vertex AO) / tangent
    /// (w = sign) / uv0+uv1, and 16- or 32-bit indices, base64. <paramref name="bone"/> maps a vertex's bone
    /// palette index (or -1 without a bone stream) to its world matrix.</summary>
    public List<Dictionary<string, object>> Groups(Buffers buf, IEnumerable<(MeshDesc M, SubMesh S)> subs,
        Func<SubMesh, int> material, Func<int, Matrix4x4> bone, float[] box)
    {
        var groups = new List<Dictionary<string, object>>();
        foreach (var (m, s) in subs)
        {
            int mat = material(s);
            if (mat < 0 || s.ICount == 0) continue;
            var st = m.StreamEaids(s);
            BufInfo Info(string e) => BufInfo.Parse(buf.Header(e));
            var pe = st.FirstOrDefault(e => Info(e).Fvf == BufInfo.FvfPosition);
            var halfs = st.Where(e => Info(e).Fvf == BufInfo.FvfHalf4).ToArray();   // normal, tangent, uv0+uv1 (+ uv2+uv3)
            // characters' cloth keeps its uv in a half2 stream instead (normal and tangent half4, uv half2): uv1 = uv0
            var uv2 = halfs.Length == 2 ? st.FirstOrDefault(e => Info(e).Fvf == BufInfo.FvfHalf2) : null;
            if (pe is null || halfs.Length < 3 && uv2 is null) continue;
            var be = st.FirstOrDefault(e => Info(e).Fvf is BufInfo.FvfBones or BufInfo.FvfBones8);
            var pos = MemoryMarshal.Cast<byte, float>(buf.Data(pe));
            var uvs = uv2 ?? halfs[2];
            var hn = buf.Data(halfs[0]); var ht = buf.Data(halfs[1]); var hu = buf.Data(uvs);
            int sn = Info(halfs[0]).Stride, stt = Info(halfs[1]).Stride, su = Info(uvs).Stride;
            // uv0 = the slot's own uv (paint sheets wrap, so it may run outside 0..1), uv1 = the decal sheet
            var bones = be is null ? null : buf.Data(be); int bs = be is null ? 0 : Info(be).Stride;
            var ib = buf.Data(m.IndexBuffer); bool wide = Info(m.IndexBuffer).Stride == 4;
            uint Idx(int i) => wide ? BitConverter.ToUInt32(ib, i * 4) : BitConverter.ToUInt16(ib, i * 2);
            float H(byte[] d, int o) => (float)BitConverter.ToHalf(d, o);

            List<float> P = []; List<Half> N = [], T = [], U = []; List<uint> I = [];
            var remap = new Dictionary<uint, uint>();
            var mats = new Dictionary<int, Matrix4x4>();
            for (int i = s.IOff; i + 2 < s.IOff + s.ICount; i += 3)
            {
                uint a = Idx(i), b = Idx(i + 1), c = Idx(i + 2);
                if (a == b || b == c || a == c) continue;   // hidden (zeroed) or degenerate
                foreach (var v in new[] { a, b, c })
                {
                    if (!remap.TryGetValue(v, out var nv))
                    {
                        int vi = (int)v + s.VOff;
                        int pal = bones is null ? -1 : bones[vi * bs];
                        if (!mats.TryGetValue(pal, out var mtx)) mats[pal] = mtx = bone(pal);
                        var p = Vector3.Transform(new Vector3(pos[vi * 3], pos[vi * 3 + 1], pos[vi * 3 + 2]), mtx);
                        var n = Vector3.Normalize(Vector3.TransformNormal(new Vector3(H(hn, vi * sn), H(hn, vi * sn + 2), H(hn, vi * sn + 4)), mtx));
                        var t = Vector3.TransformNormal(new Vector3(H(ht, vi * stt), H(ht, vi * stt + 2), H(ht, vi * stt + 4)), mtx);
                        if (float.IsNaN(n.X)) n = Vector3.UnitY;
                        nv = (uint)(P.Count / 3);
                        P.AddRange([p.X, p.Y, p.Z]);
                        N.AddRange([(Half)n.X, (Half)n.Y, (Half)n.Z, BitConverter.ToHalf(hn, vi * sn + 6)]);
                        T.AddRange([(Half)t.X, (Half)t.Y, (Half)t.Z, H(ht, vi * stt + 6) < 0 ? (Half)(-1f) : (Half)1f]);
                        for (int k = 0; k < 4; k++) U.Add(BitConverter.ToHalf(hu, vi * su + (uv2 is null ? k : k % 2) * 2));
                        box[0] = Math.Min(box[0], p.X); box[1] = Math.Min(box[1], p.Y); box[2] = Math.Min(box[2], p.Z);
                        box[3] = Math.Max(box[3], p.X); box[4] = Math.Max(box[4], p.Y); box[5] = Math.Max(box[5], p.Z);
                        remap[v] = nv;
                    }
                    I.Add(nv);
                }
            }
            if (I.Count == 0) continue;
            bool w32 = P.Count / 3 > 65535;
            var geo = new Dictionary<string, object>
            {
                ["count"] = I.Count, ["w32"] = w32,
                ["pos"] = B64(P), ["nrm"] = B64(N), ["tan"] = B64(T), ["uv"] = B64(U),
                ["idx"] = w32 ? B64(I) : B64(I.Select(x => (ushort)x).ToList()),
            };
            var gkey = string.Join("|", new[] { "pos", "nrm", "tan", "uv", "idx" }.Select(k => ((string)geo[k]).GetHashCode()))
                       + "|" + ((string)geo["pos"]).Length + "|" + ((string)geo["idx"]).Length;
            if (!_geo.TryGetValue(gkey, out var gi) || !new[] { "pos", "nrm", "tan", "uv", "idx" }.All(k => (string)Geometry[gi][k] == (string)geo[k]))
            {
                Geometry.Add(geo);
                _geo[gkey] = gi = Geometry.Count - 1;
            }
            groups.Add(new() { ["slot"] = s.Name, ["m"] = mat, ["g"] = gi });
        }
        return groups;
    }

    string B64<T>(List<T> l) where T : struct => Blob(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(l)));

    /// <summary>Binary data for the page: zlib-compressed (block textures of a livery shrink to about a fifth), then
    /// base64; the page inflates it with the browser's DecompressionStream.</summary>
    /// <summary>Where binary data goes instead of being packed into the page: a host app keeps it and returns the path
    /// it serves it at (e.g. "/blob/12"); the viewer then fetches it raw.</summary>
    public Func<byte[], string>? BlobSink { get; init; }

    string Blob(ReadOnlySpan<byte> data) => BlobSink is { } sink ? sink(data.ToArray()) : Pack(data);

    public static string Pack(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
