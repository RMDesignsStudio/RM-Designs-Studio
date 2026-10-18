using System.Numerics;
using EgoEngineLibrary.Archive.Erp;

namespace HelmetSwap.Core;

/// <summary>How a surface is drawn in the preview.</summary>
public enum SurfaceKind { Opaque, Visor, Clear }

/// <summary>One surface of the preview helmet: triangles in model space (metres), UVs, its texture and how it is drawn.</summary>
public sealed class PreviewMesh
{
    public required string Slot { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required int[] Indices { get; init; }
    public TextureImage? Texture { get; init; }
    public SurfaceKind Kind { get; init; }
}

/// <summary>
/// The preview: a driver's helmet as the game would show it (before), or with a swap applied (after), built from the
/// same game data the swap uses: the shell (LOD 0), the helmet parts model, and each surface's own colour texture.
/// Drawn by a small software renderer (no GPU): textured, lit, the visor as tinted glass and the clear parts nearly
/// see-through, over the inside of the helmet.
/// </summary>
public static class HelmetPreview
{
    /// <summary>The helmet <paramref name="target"/> would wear: their own (donor null), or with <paramref name="donor"/>'s parts.</summary>
    /// The shell follows the design (a design is painted for its own shell), as the swap builds it.
    public static List<PreviewMesh> Scene(GameInstall game, Driver target, Driver? donor, SwapParts parts = SwapParts.All, int textureSize = 1024, bool parallel = true)
    {
        Driver Pick(SwapParts p) => donor is not null && parts.HasFlag(p) ? donor : target;
        Driver shellFrom = Pick(SwapParts.Design);
        Driver design = Pick(SwapParts.Design), visor = Pick(SwapParts.Visor);

        var common = ErpX.Index(ErpX.Load(HelmetSwapper.CommonHelmetErp(game)));
        var cache = new Dictionary<Driver, Dictionary<string, ErpResource>>();
        Dictionary<string, ErpResource> Idx(Driver d)
        {
            if (cache.TryGetValue(d, out var i)) return i;
            i = ErpX.Index(ErpX.Load(d.HelmetErp));
            foreach (var f in HelmetSwapper.ReadErps(game, d)) foreach (var r in ErpX.Load(f).Resources) i.TryAdd(r.Identifier, r);
            return cache[d] = i;
        }
        // the colour texture of the material a driver uses for a slot (the first texture the material lists)
        TextureImage? Colour(Driver d, string material)
        {
            try
            {
                var idx = Idx(d);
                var id = HelmetSwapper.BoundMaterial(idx, d, material);
                var mat = idx.GetValueOrDefault(id) ?? common.GetValueOrDefault(id);
                if (mat is null) return null;
                var tex = ErpX.Eaids(ErpX.Data(mat)).FirstOrDefault(e => e.EndsWith(".tif", StringComparison.Ordinal));
                if (tex is null) return null;
                var srv = idx.GetValueOrDefault(tex) ?? common.GetValueOrDefault(tex);
                return srv is null ? null : TextureImage.Decode(game, srv, textureSize, parallel);
            }
            catch { return null; }
        }

        var meshes = new List<PreviewMesh>();
        // the shell: helmet (design), visor; the tear-offs are invisible in the game too
        var sIdx = Idx(shellFrom);
        foreach (var (slot, pos, uv, ind) in Surfaces(sIdx, HelmetSwapper.ShellRoot(shellFrom.Shell!)))
        {
            if (slot == "helmet") meshes.Add(new() { Slot = slot, Positions = pos, Uvs = uv, Indices = ind, Texture = Colour(design, "helmet"), Kind = SurfaceKind.Opaque });
            else if (slot == "visor") meshes.Add(new() { Slot = slot, Positions = pos, Uvs = uv, Indices = ind, Texture = Colour(visor, "visor"), Kind = SurfaceKind.Visor });
        }
        // the helmet parts: the wing, the visor strip and the attachments (vents, chin strip, front vents), each from the
        // donor or the target, split triangle by triangle as the swap does; every surface in its own driver's material
        var groups = new List<(Driver From, SwapParts Groups)>();
        foreach (var (p, g) in new[] { (SwapParts.Attachments, SwapParts.Attachments), (SwapParts.Wing, SwapParts.Wing), (SwapParts.VisorStrip, SwapParts.VisorStrip) })
        {
            var d = Pick(p);
            int i = groups.FindIndex(x => x.From == d);
            if (i >= 0) groups[i] = (d, groups[i].Groups | g); else groups.Add((d, g));
        }
        foreach (var (from, take) in groups)
        {
            bool hasWing = from.Spoiler != "no wing";
            Func<string, Vector3, Vector3, Vector3, bool>? keep = take == HelmetSwapper.ModelParts ? null : (slot, a, b, c) => take.HasFlag(HelmetParts.ClassOf(slot, hasWing, a, b, c));
            foreach (var (slot, pos, uv, ind) in Surfaces(Idx(from), HelmetSwapper.Attachments(from), keep))
            {
                var kind = slot == "helmet_transparent" ? SurfaceKind.Clear : SurfaceKind.Opaque;
                var material = slot == "wing" ? "helmet_wing" : slot;
                meshes.Add(new() { Slot = slot, Positions = pos, Uvs = uv, Indices = ind, Texture = kind == SurfaceKind.Clear ? null : Colour(from, material), Kind = kind });
            }
        }
        return meshes;
    }

    /// <summary>The LOD-0 surfaces of a model: slot, positions (stream 0, 3 floats), UVs (stream 3, 2 halves), triangles.</summary>
    static IEnumerable<(string Slot, Vector3[] Pos, Vector2[] Uv, int[] Ind)> Surfaces(Dictionary<string, ErpResource> idx, string root,
        Func<string, Vector3, Vector3, Vector3, bool>? keep = null)
    {
        if (!idx.TryGetValue(root + "lod=0&mesh", out var meshRes)) yield break;
        var mesh = MeshDesc.Parse(ErpX.Data(meshRes));
        var ib = ErpX.Data(idx[mesh.IndexBuffer], 1);
        for (int s = 0; s < mesh.Records.Count; s++)
        {
            if (HelmetParts.IsDecal(mesh.Slots[s])) continue;
            var streams = mesh.StreamsOf(s);
            var posData = ErpX.Data(idx[mesh.Streams[streams[0]]], 1);
            var uvData = ErpX.Data(idx[mesh.Streams[streams[3]]], 1);
            var (off, count) = mesh.IndexRange(s);
            int baseVertex = BitConverter.ToInt32(mesh.Records[s], 20);
            // compact the vertices this surface uses, keeping only the triangles asked for
            var map = new Dictionary<int, int>();
            var ind = new List<int>(count);
            var pos = new List<Vector3>(); var uv = new List<Vector2>();
            var tri = new int[3];
            for (int t = 0; t + 2 < count; t += 3)
            {
                for (int k = 0; k < 3; k++) tri[k] = BitConverter.ToUInt16(ib, (off + t + k) * 2) + baseVertex;
                if (keep is not null && !keep(mesh.Slots[s], MeshDesc.Position(posData, tri[0]), MeshDesc.Position(posData, tri[1]), MeshDesc.Position(posData, tri[2])))
                    continue;
                foreach (var v in tri)
                {
                    if (!map.TryGetValue(v, out var k))
                    {
                        k = map[v] = pos.Count;
                        pos.Add(MeshDesc.Position(posData, v));
                        uv.Add(new Vector2((float)BitConverter.ToHalf(uvData, v * 4), (float)BitConverter.ToHalf(uvData, v * 4 + 2)));
                    }
                    ind.Add(k);
                }
            }
            if (ind.Count > 0) yield return (mesh.Slots[s], pos.ToArray(), uv.ToArray(), ind.ToArray());
        }
    }

    // ------------------------------------------------------------------ renderer

    /// <summary>
    /// Draw the helmet seen from <paramref name="yawDegrees"/> (0 = from the front, 90 = from the driver's left) and a
    /// little above, into a BGRA32 image of <paramref name="width"/> x <paramref name="height"/> (2x supersampled).
    /// </summary>
    public static byte[] Render(List<PreviewMesh> scene, float yawDegrees, int width, int height, (byte R, byte G, byte B) background, float pitchDegrees = 12, float distance = 2.0f)
    {
        const int ss = 2;
        int w = width * ss, h = height * ss;
        var color = new Vector3[w * h];
        var depth = new float[w * h];
        Array.Fill(depth, float.MaxValue);
        var bg = new Vector3(background.R, background.G, background.B) / 255f;
        Array.Fill(color, bg);

        // camera: orbit around the helmet's centre
        var all = scene.SelectMany(m => m.Positions).ToArray();
        if (all.Length == 0) return new byte[width * height * 4];
        var lo = all.Aggregate(Vector3.Min); var hi = all.Aggregate(Vector3.Max);
        var centre = (lo + hi) / 2; float radius = (hi - lo).Length() / 2;
        float yaw = yawDegrees * MathF.PI / 180, pitch = pitchDegrees * MathF.PI / 180;
        // the helmet faces +z: from the front the camera sits at +z
        var eye = centre + radius * distance * new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var view = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitY);
        float fov = 2 * MathF.Atan(1.08f / 2.0f);   // the same lens at any distance: closer = bigger
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, (float)w / h, 0.05f, 10f);
        var viewProj = view * proj;
        var light = Vector3.Normalize(new Vector3(-0.45f, 0.75f, 0.6f));     // key light: above, front-left
        var fill = Vector3.Normalize(new Vector3(0.7f, 0.2f, -0.4f));

        // per-mesh smooth normals
        var normals = scene.Select(Normals).ToList();

        void Raster(PreviewMesh m, Vector3[] n, bool transparentPass)
        {
            var p = m.Positions;
            var clip = new Vector4[p.Length];
            for (int i = 0; i < p.Length; i++) clip[i] = Vector4.Transform(new Vector4(p[i], 1), viewProj);
            var order = Enumerable.Range(0, m.Indices.Length / 3).ToArray();
            if (transparentPass)   // back to front
                order = order.OrderByDescending(t => (clip[m.Indices[t * 3]].W + clip[m.Indices[t * 3 + 1]].W + clip[m.Indices[t * 3 + 2]].W)).ToArray();
            foreach (var t in order)
            {
                int i0 = m.Indices[t * 3], i1 = m.Indices[t * 3 + 1], i2 = m.Indices[t * 3 + 2];
                Vector4 c0 = clip[i0], c1 = clip[i1], c2 = clip[i2];
                if (c0.W <= 0.01f || c1.W <= 0.01f || c2.W <= 0.01f) continue;
                Vector2 s0 = Screen(c0), s1 = Screen(c1), s2 = Screen(c2);
                float area = (s1.X - s0.X) * (s2.Y - s0.Y) - (s2.X - s0.X) * (s1.Y - s0.Y);
                if (MathF.Abs(area) < 1e-6f) continue;
                bool back = area > 0;          // screen y points down: counter-clockwise in screen space = facing away
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(s0.X, MathF.Min(s1.X, s2.X))));
                int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(s0.X, MathF.Max(s1.X, s2.X))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(s0.Y, MathF.Min(s1.Y, s2.Y))));
                int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(s0.Y, MathF.Max(s1.Y, s2.Y))));
                float iw0 = 1 / c0.W, iw1 = 1 / c1.W, iw2 = 1 / c2.W;
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        var pt = new Vector2(x + 0.5f, y + 0.5f);
                        float b0 = Edge(s1, s2, pt) / area, b1 = Edge(s2, s0, pt) / area, b2 = Edge(s0, s1, pt) / area;
                        if (b0 < 0 || b1 < 0 || b2 < 0) continue;
                        // perspective-correct weights
                        float q0 = b0 * iw0, q1 = b1 * iw1, q2 = b2 * iw2, qs = q0 + q1 + q2;
                        q0 /= qs; q1 /= qs; q2 /= qs;
                        float z = q0 * c0.W + q1 * c1.W + q2 * c2.W;
                        int k = y * w + x;
                        if (z >= depth[k]) continue;
                        var nn = Vector3.Normalize(q0 * n[i0] + q1 * n[i1] + q2 * n[i2]);
                        var wp = q0 * p[i0] + q1 * p[i1] + q2 * p[i2];
                        var toEye = Vector3.Normalize(eye - wp);
                        if (Vector3.Dot(nn, toEye) < 0) nn = -nn;   // shade the side we see
                        var uv = q0 * m.Uvs[i0] + q1 * m.Uvs[i1] + q2 * m.Uvs[i2];
                        float diff = MathF.Max(0, Vector3.Dot(nn, light)) * 0.75f + MathF.Max(0, Vector3.Dot(nn, fill)) * 0.2f;
                        var half = Vector3.Normalize(light + toEye);
                        float spec = MathF.Pow(MathF.Max(0, Vector3.Dot(nn, half)), 60);
                        float fresnel = MathF.Pow(1 - MathF.Max(0, Vector3.Dot(nn, toEye)), 4);
                        if (m.Kind == SurfaceKind.Opaque)
                        {
                            Vector3 albedo;
                            if (back) albedo = new Vector3(0.10f, 0.10f, 0.11f);            // the inside of the helmet (lining)
                            else if (m.Texture is { } tex) { var s = tex.Sample(uv.X, uv.Y); albedo = new Vector3(s.R, s.G, s.B); }
                            else albedo = new Vector3(0.55f);
                            var lit = albedo * (0.32f + diff) + new Vector3(back ? 0 : 0.35f * spec + 0.12f * fresnel);
                            color[k] = Vector3.Clamp(lit, Vector3.Zero, Vector3.One);
                            depth[k] = z;
                        }
                        else
                        {
                            // glass: the visor tinted by its colour texture, the clear parts nearly colourless
                            Vector3 tint; float opacity;
                            if (m.Kind == SurfaceKind.Visor)
                            {
                                var s = m.Texture?.Sample(uv.X, uv.Y) ?? (0.25f, 0.25f, 0.25f, 1f);
                                tint = new Vector3(s.R, s.G, s.B);
                                opacity = 0.45f + 0.35f * fresnel;
                            }
                            else
                            {
                                // clear plastic: see-through, but its edges and highlights show the shape (a clear spoiler)
                                tint = new Vector3(0.80f, 0.86f, 0.93f);
                                opacity = 0.16f + 0.55f * fresnel;
                            }
                            var under = color[k];
                            var glass = Vector3.Lerp(under * (Vector3.One * 0.35f + tint * 0.65f), tint * (0.4f + diff), opacity);
                            glass += new Vector3((m.Kind == SurfaceKind.Clear ? 1.2f : 0.9f) * spec + (m.Kind == SurfaceKind.Clear ? 0.35f : 0.25f) * fresnel);
                            color[k] = Vector3.Clamp(glass, Vector3.Zero, Vector3.One);
                        }
                    }
            }

            Vector2 Screen(Vector4 c) => new((c.X / c.W * 0.5f + 0.5f) * w, (1 - (c.Y / c.W * 0.5f + 0.5f)) * h);
        }

        for (int i = 0; i < scene.Count; i++) if (scene[i].Kind == SurfaceKind.Opaque) Raster(scene[i], normals[i], false);
        // glass after everything solid, the visor before the clear trim
        foreach (var kind in new[] { SurfaceKind.Visor, SurfaceKind.Clear })
            for (int i = 0; i < scene.Count; i++) if (scene[i].Kind == kind) Raster(scene[i], normals[i], true);

        // downsample to BGRA
        var outPx = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var sum = Vector3.Zero;
                for (int dy = 0; dy < ss; dy++) for (int dx = 0; dx < ss; dx++) sum += color[(y * ss + dy) * w + x * ss + dx];
                sum /= ss * ss;
                int o = (y * width + x) * 4;
                outPx[o] = (byte)(sum.Z * 255); outPx[o + 1] = (byte)(sum.Y * 255); outPx[o + 2] = (byte)(sum.X * 255); outPx[o + 3] = 255;
            }
        return outPx;
    }

    static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);

    static Vector3[] Normals(PreviewMesh m)
    {
        var n = new Vector3[m.Positions.Length];
        // weld by position so UV seams don't show as creases
        var weld = new Dictionary<(int, int, int), Vector3>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 2000), (int)MathF.Round(p.Y * 2000), (int)MathF.Round(p.Z * 2000));
        for (int t = 0; t + 2 < m.Indices.Length; t += 3)
        {
            Vector3 a = m.Positions[m.Indices[t]], b = m.Positions[m.Indices[t + 1]], c = m.Positions[m.Indices[t + 2]];
            var fn = Vector3.Cross(b - a, c - a);
            foreach (var p in new[] { a, b, c }) { var key = Key(p); weld[key] = weld.GetValueOrDefault(key) + fn; }
        }
        for (int i = 0; i < n.Length; i++)
        {
            var v = weld.GetValueOrDefault(Key(m.Positions[i]));
            n[i] = v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitY;
        }
        return n;
    }

    /// <summary>A BGRA image as a PNG (for the CLI / tests).</summary>
    public static byte[] Png(byte[] bgra, int width, int height)
    {
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4) { rgba[i] = bgra[i + 2]; rgba[i + 1] = bgra[i + 1]; rgba[i + 2] = bgra[i]; rgba[i + 3] = bgra[i + 3]; }
        return new TextureImage { Width = width, Height = height, Rgba = rgba }.ToPng();
    }
}
