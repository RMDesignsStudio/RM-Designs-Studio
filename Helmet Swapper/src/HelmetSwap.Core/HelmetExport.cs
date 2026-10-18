using System.Globalization;
using System.Numerics;
using System.Text;
using EgoEngineLibrary.Archive.Erp;

namespace HelmetSwap.Core;

/// <summary>
/// The helmet as a 3D model for Substance Painter / Blender: the helmet the swap makes (or a driver's own), one object
/// and material per texture set, named after the game's helmet surface, the same for every helmet (as other helmet
/// exporters do), so a Painter project re-imports any exported helmet and keeps its texture sets:
/// <code>
/// helmet                the shell's painted surface        textures/charles_leclerc_helmet_d.png
/// helmet_wing           the wing (and Schuberth front vents)  ..._helmet_wing_d.png
/// visor_attachments     the visor strip                    ..._visor_attachments_d.png
/// visor                 the visor                          ..._visor_d.png
/// helmet_attachments    front vents from another driver than the wing (when the swap moves them)
/// visor_tear            the tear-off films over the visor          (blank in the game: UV layout only)
/// helmet_transparent    the clear plastic parts (vents, clear wings) (blank in the game: UV layout only)
/// </code>
/// Written as FBX (binary 7.4) and OBJ + MTL, metres, Y up, with each set's colour / normal / surface maps at full
/// resolution as PNG (textures/: the game's maps only) and a UV wireframe per set (UVs/).
/// The clear parts and the tear-offs use the game's blank placeholder textures, so they get a material and a UV layout
/// but no maps.
/// </summary>
public static class HelmetExport
{
    public sealed class Part
    {
        public required string Set { get; init; }               // "helmet", "helmet_wing", ...
        public required Driver Source { get; init; }            // whose material the set wears
        public required string Material { get; init; }          // that driver's material slot ("helmet_wing" for vents)
        public List<Vector3> Positions { get; } = [];
        public List<Vector3> Normals { get; } = [];
        public List<Vector2> Uvs { get; } = [];
        public List<int> Indices { get; } = [];
    }

    static readonly string[] SetOrder = ["helmet", "helmet_wing", "visor_attachments", "helmet_attachments", "visor", "visor_tear", "helmet_transparent"];

    /// <summary>Sets whose material only has the game's blank placeholder textures: no maps, a UV layout only.</summary>
    static bool Untextured(string set) => set is "helmet_transparent" or "visor_tear";

    /// <summary>The helmet <paramref name="target"/> wears after the swap (donor null: their own), as texture sets.</summary>
    public static List<Part> Build(GameInstall game, Driver target, Driver? donor, SwapParts parts = SwapParts.All) =>
        Build(game, target, donor is null ? null : PartPicks.Of(donor, parts));

    /// <summary>The helmet <paramref name="target"/> wears with each picked part from its helmet (null: their own).</summary>
    public static List<Part> Build(GameInstall game, Driver target, PartPicks? picks)
    {
        Driver Pick(SwapParts p) => picks?[p] ?? target;
        Driver design = Pick(SwapParts.Design), visor = Pick(SwapParts.Visor), wingFrom = Pick(SwapParts.Wing);
        var cache = new Dictionary<Driver, Dictionary<string, ErpResource>>();
        Dictionary<string, ErpResource> Idx(Driver d)
        {
            if (cache.TryGetValue(d, out var i)) return i;
            i = ErpX.Index(ErpX.Load(d.HelmetErp));
            foreach (var f in HelmetSwapper.ReadErps(game, d)) foreach (var r in ErpX.Load(f).Resources) i.TryAdd(r.Identifier, r);
            return cache[d] = i;
        }
        var sets = new Dictionary<string, Part>();
        Part Set(string set, Driver src, string material) =>
            sets.TryGetValue(set, out var p) ? p : sets[set] = new Part { Set = set, Source = src, Material = material };

        // the shell (it follows the design): its painted surface and the visor
        foreach (var s in Surfaces(Idx(design), HelmetSwapper.ShellRoot(design.Shell!), null))
            if (s.Slot == "helmet") Add(Set("helmet", design, "helmet"), s);
            else if (s.Slot == "visor") Add(Set("visor", visor, "visor"), s);
            else if (s.Slot == "visor_tear") Add(Set("visor_tear", visor, "visor_tear"), s);
        // the helmet parts model: wing, visor strip and attachments, each from its driver (split as the swap does)
        var groups = new List<(Driver From, SwapParts Groups)>();
        foreach (var p in new[] { SwapParts.Wing, SwapParts.VisorStrip, SwapParts.Attachments })
        {
            var d = Pick(p);
            int i = groups.FindIndex(x => x.From == d);
            if (i >= 0) groups[i] = (d, groups[i].Groups | p); else groups.Add((d, p));
        }
        foreach (var (from, take) in groups)
        {
            bool hasWing = from.Spoiler != "no wing";
            Func<string, Vector3, Vector3, Vector3, bool>? keep = take == HelmetSwapper.ModelParts ? null : (slot, a, b, c) => take.HasFlag(HelmetParts.ClassOf(slot, hasWing, a, b, c));
            foreach (var s in Surfaces(Idx(from), HelmetSwapper.Attachments(from), keep))
            {
                var part = s.Slot switch
                {
                    // the wing texture also paints the front vents; vents from another driver than the wing get a set of their own
                    "wing" => from == wingFrom ? Set("helmet_wing", from, "helmet_wing") : Set("helmet_attachments", from, "helmet_wing"),
                    "visor_attachments" => Set("visor_attachments", from, "visor_attachments"),
                    "helmet_transparent" => Set("helmet_transparent", from, "helmet_transparent"),
                    _ => null,
                };
                if (part is not null) Add(part, s);
            }
        }
        return [.. SetOrder.Where(sets.ContainsKey).Select(k => sets[k])];
    }

    static void Add(Part p, (string Slot, Vector3[] Pos, Vector3[] Nrm, Vector2[] Uv, int[] Ind) s)
    {
        int b = p.Positions.Count;
        p.Positions.AddRange(s.Pos); p.Normals.AddRange(s.Nrm); p.Uvs.AddRange(s.Uv);
        p.Indices.AddRange(s.Ind.Select(i => i + b));
    }

    /// <summary>A model's LOD-0 surfaces: slot, positions, normals, UVs (game space: v down), triangles; streams found by type.</summary>
    static IEnumerable<(string Slot, Vector3[] Pos, Vector3[] Nrm, Vector2[] Uv, int[] Ind)> Surfaces(Dictionary<string, ErpResource> idx, string root,
        Func<string, Vector3, Vector3, Vector3, bool>? keep)
    {
        if (!idx.TryGetValue(root + "lod=0&mesh", out var meshRes)) yield break;
        var mesh = MeshDesc.Parse(ErpX.Data(meshRes));
        if (!idx.TryGetValue(mesh.IndexBuffer, out var ibr)) yield break;
        var ib = ErpX.Data(ibr, 1);
        bool wideIb = BitConverter.ToInt32(ErpX.Data(ibr, 0), 4) == 4;
        for (int s = 0; s < mesh.Records.Count; s++)
        {
            if (HelmetParts.IsDecal(mesh.Slots[s])) continue;
            byte[]? pos = null, nrm = null, uvs = null; int sn = 8, su = 4;
            foreach (var k in mesh.StreamsOf(s))
            {
                if (!idx.TryGetValue(mesh.Streams[k], out var vr)) continue;
                var hdr = ErpX.Data(vr, 0);
                int stride = BitConverter.ToInt32(hdr, 4), type = BitConverter.ToInt32(hdr, 12);
                if (type == 0x27 && pos is null) pos = ErpX.Data(vr, 1);
                else if (type == 0x23 && nrm is null) { nrm = ErpX.Data(vr, 1); sn = stride; }
                else if (type == 0x1D && uvs is null) { uvs = ErpX.Data(vr, 1); su = stride; }
            }
            if (pos is null || nrm is null || uvs is null) continue;
            var (off, count) = mesh.IndexRange(s);
            int bv = BitConverter.ToInt32(mesh.Records[s], 20);
            int Index(int i) => (wideIb ? (int)BitConverter.ToUInt32(ib, i * 4) : BitConverter.ToUInt16(ib, i * 2)) + bv;
            var map = new Dictionary<int, int>();
            List<Vector3> P = [], N = []; List<Vector2> U = []; List<int> I = [];
            var tri = new int[3];
            for (int t = 0; t + 2 < count; t += 3)
            {
                for (int k = 0; k < 3; k++) tri[k] = Index(off + t + k);
                if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2]) continue;
                if (keep is not null && !keep(mesh.Slots[s], MeshDesc.Position(pos, tri[0]), MeshDesc.Position(pos, tri[1]), MeshDesc.Position(pos, tri[2]))) continue;
                foreach (var v in tri)
                {
                    if (!map.TryGetValue(v, out var nv))
                    {
                        nv = map[v] = P.Count;
                        P.Add(MeshDesc.Position(pos, v));
                        var n = new Vector3((float)BitConverter.ToHalf(nrm, v * sn), (float)BitConverter.ToHalf(nrm, v * sn + 2), (float)BitConverter.ToHalf(nrm, v * sn + 4));
                        N.Add(n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY);
                        U.Add(new Vector2((float)BitConverter.ToHalf(uvs, v * su), (float)BitConverter.ToHalf(uvs, v * su + 2)));
                    }
                    I.Add(nv);
                }
            }
            if (I.Count > 0) yield return (mesh.Slots[s], P.ToArray(), N.ToArray(), U.ToArray(), I.ToArray());
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>"charles_leclerc": the name stem of the helmet's files and texture sets.</summary>
    public static string Stem(Driver target) => target.Token.ToLowerInvariant();

    /// <summary>
    /// Write the model into <paramref name="dir"/>: &lt;stem&gt;_helmet.fbx / .obj / .mtl, textures/ (full-resolution
    /// colour, normal and surface maps, and the colour map with its UV wireframe), UVs/ (the wireframes alone).
    /// Returns the files written.
    /// </summary>
    public static List<string> Write(GameInstall game, Driver target, Driver? donor, SwapParts parts, string dir, SwapLog log) =>
        Write(game, target, donor is null ? null : PartPicks.Of(donor, parts), dir, log);

    public static List<string> Write(GameInstall game, Driver target, PartPicks? picks, string dir, SwapLog log)
    {
        var sets = Build(game, target, picks);
        var stem = Stem(target);
        Directory.CreateDirectory(Path.Combine(dir, "textures"));
        Directory.CreateDirectory(Path.Combine(dir, "UVs"));
        var written = new List<string>();
        var maps = new Dictionary<string, (string? D, string? N, string? S)>();
        foreach (var p in sets)
        {
            maps[p.Set] = WriteTextures(game, p, stem, dir, written, log);
            log.Info($"{MaterialName(p)}: {p.Indices.Count / 3} triangles, {p.Source.Name}'s {p.Material} material");
        }
        var obj = Path.Combine(dir, $"{stem}_helmet.obj");
        WriteObj(sets, maps, stem, obj); written.Add(obj); written.Add(Path.ChangeExtension(obj, ".mtl"));
        var fbx = Path.Combine(dir, $"{stem}_helmet.fbx");
        File.WriteAllBytes(fbx, Fbx.Write(sets, maps, stem)); written.Add(fbx);
        return written;
    }

    /// <summary>"helmet", "helmet_wing", "visor"...: the same for every helmet, so Painter matches its texture sets when
    /// a project's mesh is swapped for another exported helmet.</summary>
    public static string MaterialName(Part p) => p.Set;

    /// <summary>The set's maps as PNG (relative paths), its UV wireframe and the colour map with the wireframe.</summary>
    static (string? D, string? N, string? S) WriteTextures(GameInstall game, Part p, string stem, string dir, List<string> written, SwapLog log)
    {
        if (Untextured(p.Set))
        {
            // the clear parts and tear-offs wear blank placeholders: just their UV layout, to paint on if wanted
            var uvPath = Path.Combine(dir, "UVs", $"{stem}_{p.Set}_uv.png");
            File.WriteAllBytes(uvPath, UvWireframe(p, 2048, 2048).ToPng()); written.Add(uvPath);
            return (null, null, null);
        }
        var idx = ErpX.Index(ErpX.Load(p.Source.HelmetErp));
        foreach (var f in HelmetSwapper.ReadErps(game, p.Source)) foreach (var r in ErpX.Load(f).Resources) idx.TryAdd(r.Identifier, r);
        var common = ErpX.Index(ErpX.Load(HelmetSwapper.CommonHelmetErp(game)));
        var matId = HelmetSwapper.BoundMaterial(idx, p.Source, p.Material);
        var mat = EgoMaterial.Parse(ErpX.Data(idx.GetValueOrDefault(matId) ?? common[matId]));
        string? Map(string sampler, string kind)
        {
            if (mat.Textures.GetValueOrDefault(sampler) is not { } id || (idx.GetValueOrDefault(id) ?? common.GetValueOrDefault(id)) is not { } srv) return null;
            // the game's shared placeholder maps (8 x 8 "charname_" normal / surface maps) aren't the helmet's: left out
            if (kind != "d" && !p.Source.OwnsTexture(id)) return null;
            var img = TextureImage.Decode(game, srv, 16384);
            if (kind == "n") ReconstructNormalZ(img);
            var rel = $"textures/{stem}_{p.Set}_{kind}.png";
            File.WriteAllBytes(Path.Combine(dir, rel), img.ToPng()); written.Add(Path.Combine(dir, rel));
            if (kind == "d")
            {
                // the UV layout on its own (UVs/), the colour map's size; tiny maps (a visor tint is 8 x 8 or 512 x 64)
                // get it at least 1024 px on the short side
                int scale = Math.Max(1, (int)Math.Ceiling(1024.0 / Math.Min(img.Width, img.Height)));
                var uvPath = Path.Combine(dir, "UVs", $"{stem}_{p.Set}_uv.png");
                File.WriteAllBytes(uvPath, UvWireframe(p, img.Width * scale, img.Height * scale).ToPng()); written.Add(uvPath);
            }
            return rel;
        }
        return (Map("me_diffuseTex", "d"), Map("me_normalTex", "n"), Map("me_surfaceTex", "s"));
    }

    /// <summary>The game's normal maps keep X and Y (BC5); Z is rebuilt so the PNG reads as an ordinary normal map.</summary>
    static void ReconstructNormalZ(TextureImage img)
    {
        var d = img.Rgba;
        for (int i = 0; i < d.Length; i += 4)
        {
            float x = d[i] / 127.5f - 1, y = d[i + 1] / 127.5f - 1;
            d[i + 2] = (byte)Math.Clamp(MathF.Round((MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y)) + 1) * 127.5f), 0, 255);
            d[i + 3] = 255;
        }
    }

    /// <summary>The set's UV layout: every triangle edge as a 1-px white line on transparent (texture space, v down).</summary>
    public static TextureImage UvWireframe(Part p, int w, int h)
    {
        var px = new byte[w * h * 4];
        void Plot(int x, int y) { if ((uint)x >= w || (uint)y >= h) return; int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255; }
        void Line(Vector2 a, Vector2 b)
        {
            float x0 = Frac(a.X) * w, y0 = Frac(a.Y) * h, x1 = x0 + (b.X - a.X) * w, y1 = y0 + (b.Y - a.Y) * h;
            int n = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0))) + 1;
            if (n > 4 * (w + h)) return;
            for (int k = 0; k <= n; k++) { float t = (float)k / n; Plot((int)(x0 + (x1 - x0) * t), (int)(y0 + (y1 - y0) * t)); }
        }
        // a triangle whose UVs lie in another tile (wrapping) is drawn in the first one
        static float Frac(float v) => v - MathF.Floor(v);
        for (int t = 0; t + 2 < p.Indices.Count; t += 3)
        {
            Vector2 a = p.Uvs[p.Indices[t]], b = p.Uvs[p.Indices[t + 1]], c = p.Uvs[p.Indices[t + 2]];
            var shift = new Vector2(MathF.Floor(a.X), MathF.Floor(a.Y));
            Line(a - shift, b - shift); Line(b - shift, c - shift); Line(c - shift, a - shift);
        }
        return new TextureImage { Width = w, Height = h, Rgba = px };
    }

    static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>OBJ + MTL: one object and material per set; v flipped (OBJ's v runs up).</summary>
    static void WriteObj(List<Part> sets, Dictionary<string, (string? D, string? N, string? S)> maps, string stem, string path)
    {
        var mtlName = Path.GetFileName(Path.ChangeExtension(path, ".mtl"));
        var o = new StringBuilder();
        o.Append("# RM Designs Helmet Swapper - ").Append(stem).Append(" helmet (metres, Y up)\n");
        o.Append("mtllib ").Append(mtlName).Append('\n');
        int baseV = 1;
        foreach (var p in sets)
        {
            var name = MaterialName(p);
            o.Append("o ").Append(name).Append('\n');
            foreach (var v in p.Positions) o.Append("v ").Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z)).Append('\n');
            foreach (var t in p.Uvs) o.Append("vt ").Append(F(t.X)).Append(' ').Append(F(1 - t.Y)).Append('\n');
            foreach (var n in p.Normals) o.Append("vn ").Append(F(n.X)).Append(' ').Append(F(n.Y)).Append(' ').Append(F(n.Z)).Append('\n');
            o.Append("usemtl ").Append(name).Append("\ns 1\n");
            for (int t = 0; t + 2 < p.Indices.Count; t += 3)
            {
                o.Append('f');
                for (int k = 0; k < 3; k++) { int i = p.Indices[t + k] + baseV; o.Append(' ').Append(i).Append('/').Append(i).Append('/').Append(i); }
                o.Append('\n');
            }
            baseV += p.Positions.Count;
        }
        File.WriteAllText(path, o.ToString());

        var m = new StringBuilder();
        foreach (var p in sets)
        {
            var (d, n, s) = maps[p.Set];
            m.Append("newmtl ").Append(MaterialName(p)).Append('\n');
            m.Append("Ka 0 0 0\nKd 0.8 0.8 0.8\nKs 0.2 0.2 0.2\nNs 50\n");
            if (Untextured(p.Set)) m.Append(p.Set == "visor_tear" ? "d 0.1\n" : "d 0.25\n");
            else
            {
                m.Append("d 1\n");
                if (d is not null) m.Append("map_Kd ").Append(d).Append('\n');
                if (n is not null) m.Append("map_Bump ").Append(n).Append('\n');
            }
            m.Append('\n');
        }
        File.WriteAllText(Path.ChangeExtension(path, ".mtl"), m.ToString());
    }

    /// <summary>A minimal binary FBX 7.4 writer: meshes with normals and one UV set, one material each, the colour map
    /// connected as its diffuse texture (and the normal map as its normal map). Metres (unit scale 100 cm), Y up.</summary>
    static class Fbx
    {
        sealed class Node(string name)
        {
            public string Name = name;
            public List<object> Props = [];
            public List<Node> Children = [];
            public Node Add(string name, params object[] props) { var n = new Node(name); n.Props.AddRange(props); Children.Add(n); return n; }
        }

        static Node P70(Node parent) => parent.Add("Properties70");
        static void P(Node p70, string name, string type, string label, string flags, params object[] values) =>
            p70.Add("P", [name, type, label, flags, .. values]);

        public static byte[] Write(List<Part> sets, Dictionary<string, (string? D, string? N, string? S)> maps, string stem)
        {
            long id = 1_000_000;
            var root = new List<Node>();
            var header = new Node("FBXHeaderExtension"); root.Add(header);
            header.Add("FBXHeaderVersion", 1003);
            header.Add("FBXVersion", 7400);
            header.Add("Creator", "RM Designs Helmet Swapper");
            root.Add(new Node("FileId") { Props = { new Raw([0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1]) } });
            root.Add(new Node("CreationTime") { Props = { "1970-01-01 10:00:00:000" } });
            root.Add(new Node("Creator") { Props = { "RM Designs Helmet Swapper" } });

            var gs = new Node("GlobalSettings"); root.Add(gs);
            gs.Add("Version", 1000);
            var g = P70(gs);
            P(g, "UpAxis", "int", "Integer", "", 1); P(g, "UpAxisSign", "int", "Integer", "", 1);
            P(g, "FrontAxis", "int", "Integer", "", 2); P(g, "FrontAxisSign", "int", "Integer", "", 1);
            P(g, "CoordAxis", "int", "Integer", "", 0); P(g, "CoordAxisSign", "int", "Integer", "", 1);
            P(g, "OriginalUpAxis", "int", "Integer", "", 1); P(g, "OriginalUpAxisSign", "int", "Integer", "", 1);
            P(g, "UnitScaleFactor", "double", "Number", "", 100.0); P(g, "OriginalUnitScaleFactor", "double", "Number", "", 100.0);

            var docs = new Node("Documents"); root.Add(docs); docs.Add("Count", 1);
            var doc = docs.Add("Document", id++, "Scene", "Scene");
            P70(doc);
            doc.Add("RootNode", 0L);
            root.Add(new Node("References"));

            var objects = new Node("Objects");
            var conns = new Node("Connections");
            int nTex = 0;
            foreach (var p in sets)
            {
                var name = MaterialName(p);
                long geomId = id++, modelId = id++, matId = id++;
                // geometry: positions, triangles (last index of a polygon stored as -(i+1)), normals and UVs per corner
                var geom = objects.Add("Geometry", geomId, name + "\0\u0001Geometry", "Mesh");
                geom.Add("Vertices", p.Positions.SelectMany(v => new[] { (double)v.X, v.Y, v.Z }).ToArray());
                var pvi = new int[p.Indices.Count];
                for (int t = 0; t + 2 < p.Indices.Count; t += 3) { pvi[t] = p.Indices[t]; pvi[t + 1] = p.Indices[t + 1]; pvi[t + 2] = -p.Indices[t + 2] - 1; }
                geom.Add("PolygonVertexIndex", pvi);
                geom.Add("GeometryVersion", 124);
                var ln = geom.Add("LayerElementNormal", 0);
                ln.Add("Version", 101); ln.Add("Name", ""); ln.Add("MappingInformationType", "ByPolygonVertex"); ln.Add("ReferenceInformationType", "Direct");
                ln.Add("Normals", p.Indices.SelectMany(i => new[] { (double)p.Normals[i].X, p.Normals[i].Y, p.Normals[i].Z }).ToArray());
                var lu = geom.Add("LayerElementUV", 0);
                lu.Add("Version", 101); lu.Add("Name", "UVMap"); lu.Add("MappingInformationType", "ByPolygonVertex"); lu.Add("ReferenceInformationType", "IndexToDirect");
                lu.Add("UV", p.Uvs.SelectMany(t => new[] { (double)t.X, 1.0 - t.Y }).ToArray());
                lu.Add("UVIndex", p.Indices.ToArray());
                var lm = geom.Add("LayerElementMaterial", 0);
                lm.Add("Version", 101); lm.Add("Name", ""); lm.Add("MappingInformationType", "AllSame"); lm.Add("ReferenceInformationType", "IndexToDirect");
                lm.Add("Materials", new[] { 0 });
                var layer = geom.Add("Layer", 0);
                layer.Add("Version", 100);
                foreach (var type in new[] { "LayerElementNormal", "LayerElementMaterial", "LayerElementUV" })
                {
                    var le = layer.Add("LayerElement");
                    le.Add("Type", type); le.Add("TypedIndex", 0);
                }

                var model = objects.Add("Model", modelId, name + "\0\u0001Model", "Mesh");
                model.Add("Version", 232);
                var mp = P70(model);
                P(mp, "DefaultAttributeIndex", "int", "Integer", "", 0);
                model.Add("Shading", true);
                model.Add("Culling", "CullingOff");

                var mat = objects.Add("Material", matId, name + "\0\u0001Material", "");
                mat.Add("Version", 102);
                mat.Add("ShadingModel", "phong");
                mat.Add("MultiLayer", 0);
                var matp = P70(mat);
                P(matp, "DiffuseColor", "Color", "", "A", 0.8, 0.8, 0.8);
                P(matp, "SpecularColor", "Color", "", "A", 0.2, 0.2, 0.2);
                P(matp, "Shininess", "double", "Number", "", 20.0);
                if (Untextured(p.Set)) P(matp, "TransparencyFactor", "Number", "", "A", p.Set == "visor_tear" ? 0.9 : 0.75);

                conns.Add("C", "OO", modelId, 0L);
                conns.Add("C", "OO", geomId, modelId);
                conns.Add("C", "OO", matId, modelId);

                var (d, n, _) = maps[p.Set];
                foreach (var (file, prop) in new[] { (d, "DiffuseColor"), (n, "NormalMap") })
                {
                    if (file is null) continue;
                    long texId = id++, vidId = id++;
                    var texName = Path.GetFileNameWithoutExtension(file);
                    var vid = objects.Add("Video", vidId, texName + "\0\u0001Video", "Clip");
                    vid.Add("Type", "Clip");
                    P(P70(vid), "Path", "KString", "XRefUrl", "", file);
                    vid.Add("UseMipMap", 0);
                    vid.Add("Filename", file);
                    vid.Add("RelativeFilename", file);
                    var tex = objects.Add("Texture", texId, texName + "\0\u0001Texture", "");
                    tex.Add("Type", "TextureVideoClip");
                    tex.Add("Version", 202);
                    tex.Add("TextureName", texName + "\0\u0001Texture");
                    var tp = P70(tex);
                    P(tp, "UseMaterial", "bool", "", "", 1);
                    tex.Add("Media", texName + "\0\u0001Video");
                    tex.Add("FileName", file);
                    tex.Add("RelativeFilename", file);
                    tex.Add("ModelUVTranslation", 0.0, 0.0);
                    tex.Add("ModelUVScaling", 1.0, 1.0);
                    tex.Add("Texture_Alpha_Source", "None");
                    tex.Add("Cropping", 0, 0, 0, 0);
                    conns.Add("C", "OO", vidId, texId);
                    conns.Add("C", "OP", texId, matId, prop);
                    nTex++;
                }
            }

            var defs = new Node("Definitions"); root.Add(defs);
            defs.Add("Version", 100);
            defs.Add("Count", 1 + sets.Count * 3 + nTex * 2);
            defs.Add("ObjectType", "GlobalSettings").Add("Count", 1);
            defs.Add("ObjectType", "Model").Add("Count", sets.Count);
            defs.Add("ObjectType", "Geometry").Add("Count", sets.Count);
            defs.Add("ObjectType", "Material").Add("Count", sets.Count);
            if (nTex > 0) { defs.Add("ObjectType", "Texture").Add("Count", nTex); defs.Add("ObjectType", "Video").Add("Count", nTex); }
            root.Add(objects);
            root.Add(conns);
            var takes = new Node("Takes"); takes.Add("Current", ""); root.Add(takes);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0"));
            w.Write((byte)0x1A); w.Write((byte)0x00);
            w.Write(7400u);
            foreach (var n in root) WriteNode(w, n);
            w.Write(new byte[13]);                                  // end of the top-level list
            // footer, as Blender's and Autodesk's writers lay it out
            w.Write(new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e });
            w.Write(new byte[4]);
            int pad = (int)((16 - (ms.Position % 16)) % 16); if (pad == 0) pad = 16;
            w.Write(new byte[pad]);
            w.Write(7400u);
            w.Write(new byte[120]);
            w.Write(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });
            w.Flush();
            return ms.ToArray();
        }

        sealed record Raw(byte[] Data);

        static void WriteNode(BinaryWriter w, Node n)
        {
            var s = w.BaseStream;
            long start = s.Position;
            w.Write(0u); w.Write((uint)n.Props.Count); w.Write(0u);
            var nameBytes = Encoding.ASCII.GetBytes(n.Name);
            w.Write((byte)nameBytes.Length); w.Write(nameBytes);
            long propStart = s.Position;
            foreach (var p in n.Props) WriteProp(w, p);
            long propEnd = s.Position;
            // a node with children (or none at all and no properties) ends its list with a 13-byte null record
            if (n.Children.Count > 0 || n.Props.Count == 0)
            {
                foreach (var c in n.Children) WriteNode(w, c);
                w.Write(new byte[13]);
            }
            long end = s.Position;
            s.Position = start; w.Write((uint)end); w.Write((uint)n.Props.Count); w.Write((uint)(propEnd - propStart));
            s.Position = end;
        }

        static void WriteProp(BinaryWriter w, object p)
        {
            switch (p)
            {
                case bool b: w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
                case short v: w.Write((byte)'Y'); w.Write(v); break;
                case int v: w.Write((byte)'I'); w.Write(v); break;
                case long v: w.Write((byte)'L'); w.Write(v); break;
                case float v: w.Write((byte)'F'); w.Write(v); break;
                case double v: w.Write((byte)'D'); w.Write(v); break;
                case string v: { var b = Encoding.UTF8.GetBytes(v); w.Write((byte)'S'); w.Write((uint)b.Length); w.Write(b); break; }
                case Raw r: w.Write((byte)'R'); w.Write((uint)r.Data.Length); w.Write(r.Data); break;
                case double[] a: w.Write((byte)'d'); w.Write((uint)a.Length); w.Write(0u); w.Write((uint)(a.Length * 8)); foreach (var x in a) w.Write(x); break;
                case int[] a: w.Write((byte)'i'); w.Write((uint)a.Length); w.Write(0u); w.Write((uint)(a.Length * 4)); foreach (var x in a) w.Write(x); break;
                default: throw new InvalidOperationException($"FBX property type {p.GetType().Name}");
            }
        }
    }
}
