using HelmetSwap.Core;

// helmetswap drivers [--game DIR]
// helmetswap templates --out DIR [--drivers name,name] [--game DIR]
// exit codes: 0 ok, 1 usage, 2 failed
if (args.Length < 1) { Console.WriteLine("usage: helmetswap drivers|templates [--out DIR] [--drivers a,b] [--game DIR]"); return 1; }
string? Opt(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

try
{
    var game = new GameInstall(Opt("--game") ?? GameInstall.Detect() ?? throw new DirectoryNotFoundException("F1 25 not found; pass --game"));
    // older seasons' helmets (donors only) for: drivers --all, swap, scan-like analysis; templates stay 2026
    var year = Opt("--year") is { } ys ? int.Parse(ys) : (int?)null;
    var drivers = Drivers.Find(game, olderHelmets: args[0] is "swap" or "export" || args.Contains("--all") || year is not null, career: args.Contains("--career") || args[0] is "swap" or "export");
    var filters = (Opt("--drivers") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    var series = Opt("--series");
    var sel = drivers.Where(d => series is null || d.Series.Equals(series, StringComparison.OrdinalIgnoreCase)).Where(d => filters.Length == 0 || filters.Any(f => d.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Token.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();

    // --mix design:a,visor:b,wing:c: parts from several helmets (preview --page, export, swap)
    PartPicks? MixOpt()
    {
        if (Opt("--mix") is not { } mix) return null;
        var picks = new PartPicks();
        foreach (var pair in mix.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (part, who) = (pair[..pair.IndexOf(':')].Trim(), pair[(pair.IndexOf(':') + 1)..].Trim());
            picks[Enum.Parse<SwapParts>(part, ignoreCase: true)] = drivers.Single(d => d.Folder == who || d.Token == who);
        }
        return picks;
    }

    if (args[0] == "drivers")
    {
        foreach (var d in drivers) Console.WriteLine($"{d.Year} {d.Series} {d.Team.Name,-22} {d.Name + (d.Variant is null ? "" : " (" + d.Variant + ")"),-36} {d.ShellName,-20} {d.Spoiler,-11} {d.Folder}");
        Console.WriteLine($"{drivers.Count} drivers");
        return 0;
    }
    if (args[0] == "templates")
    {
        // helmet templates for the drivers that can be changed: 2026 (default) or --year 2025
        var y = year ?? Drivers.Season;
        if (y < Drivers.FirstChangeableYear) throw new ArgumentException($"templates are made for {Drivers.FirstChangeableYear}-{Drivers.Season} drivers only");
        sel = sel.Where(d => d.Year == y).ToList();
        var outDir = Opt("--out") ?? throw new ArgumentException("--out DIR required");
        Directory.CreateDirectory(outDir);
        int total = 0;
        foreach (var d in sel)
        {
            var log = new SwapLog();
            var erp = HelmetTemplate.BuildErp(game, d, log);
            var path = Path.Combine(outDir, HelmetTemplate.FileName(d));
            HelmetTemplate.Write(path, d, erp);
            var failures = HelmetTemplate.Verify(game, d, path, out var checks);
            if (failures.Count > 0)
            {
                File.Delete(path);
                throw new InvalidDataException($"{d.Name}: verification failed:\n  " + string.Join("\n  ", failures));
            }
            total += checks;
            Console.WriteLine($"{d.Name,-24} {log.All.Count} textures, {new FileInfo(path).Length / (1024 * 1024.0):F1} MB, {checks} checks passed  -> {Path.GetFileName(path)}");
        }
        Console.WriteLine($"Done: {sel.Count} templates, {total} checks passed.");
        return 0;
    }
    if (args[0] == "dump")
    {
        // helmetswap dump --drivers a [--match text]: resources of the driver's helmet + main ERP; eaids of matching ones
        var match = Opt("--match");
        var files = Opt("--file") is { } one ? [Path.Combine(game.Assets, one)] : sel.SelectMany(d => new[] { d.HelmetErp, HelmetSwapper.MainErp(d) }.Where(File.Exists));
        foreach (var path in files)
            {
                Console.WriteLine($"== {Path.GetFileName(path)}");
                foreach (var r in ErpX.Load(path).Resources)
                {
                    var sizes = string.Join("/", r.Fragments.Select(f => f.Size));
                    Console.WriteLine($"{r.ResourceType,-22} {sizes,-22} {r.Identifier}");
                    if (match is not null && r.Identifier.Contains(match, StringComparison.OrdinalIgnoreCase))
                    {
                        var data = ErpX.Data(r);
                        if (args.Contains("--strings"))
                            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(data), @"[\x20-\x7e]{4,}"))
                                Console.WriteLine($"      @{m.Index,-6} {m.Value}");
                        else foreach (var e in ErpX.Eaids(data).Distinct()) Console.WriteLine("      -> " + e);
                        if (args.Contains("--hex")) Console.WriteLine("      " + Convert.ToHexString(data));
                    }
                }
            }
        return 0;
    }
    if (args[0] == "shaders")
    {
        // helmetswap shaders --dir character_package: every material's shader (first eaid), counted, with an example
        var root = Path.Combine(game.Assets, Opt("--dir") ?? "character_package");
        var seen = new Dictionary<string, (int N, string Example)>();
        foreach (var f in Directory.EnumerateFiles(root, "*.erp", SearchOption.AllDirectories))
        {
            try
            {
                foreach (var r in ErpX.Load(f).Resources.Where(r => r.ResourceType == "GfxMaterialRes"))
                {
                    var sh = ErpX.Eaids(ErpX.Data(r)).FirstOrDefault() ?? "(none)";
                    seen[sh] = seen.TryGetValue(sh, out var v) ? (v.N + 1, v.Example) : (1, r.Identifier);
                }
            }
            catch { }
        }
        foreach (var (k, v) in seen.OrderBy(x => x.Key)) Console.WriteLine($"{v.N,6}  {k}   e.g. {v.Example}");
        return 0;
    }
    if (args[0] == "erpdiff")
    {
        // helmetswap erpdiff A.erp|A.serps B.erp|B.serps: resources only in A / only in B / different content (read-only;
        // a .serps = all its ERPs together)
        static Dictionary<string, EgoEngineLibrary.Archive.Erp.ErpResource> Idx(string path)
        {
            if (!path.EndsWith(".serps", StringComparison.OrdinalIgnoreCase)) return ErpX.Index(ErpX.Load(path));
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return HelmetSerps.ReadAll(zip, (ok, msg) => { if (!ok) Console.WriteLine("! " + msg); }, out _);
        }
        var a = Idx(args[1]); var b = Idx(args[2]);
        string Sizes(EgoEngineLibrary.Archive.Erp.ErpResource r) => string.Join("/", r.Fragments.Select(f => f.Size));
        string Hash(EgoEngineLibrary.Archive.Erp.ErpResource r) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            r.Fragments.SelectMany((f, i) => ErpX.Data(r, i)).ToArray()))[..8];
        Console.WriteLine($"A {a.Count} resources, B {b.Count} resources");
        foreach (var k in a.Keys.Except(b.Keys).Order()) Console.WriteLine($"only A  {a[k].ResourceType,-18} {Sizes(a[k]),-20} {k}");
        foreach (var k in b.Keys.Except(a.Keys).Order()) Console.WriteLine($"only B  {b[k].ResourceType,-18} {Sizes(b[k]),-20} {k}");
        foreach (var k in a.Keys.Intersect(b.Keys).Order())
            if (Hash(a[k]) != Hash(b[k])) Console.WriteLine($"diff    {a[k].ResourceType,-18} {Sizes(a[k]),-20} -> {Sizes(b[k]),-20} {k}");
        return 0;
    }
    if (args[0] == "scan")
    {
        // every driver folder of any season: helmet ERP, shell, attachments model, own bindings, main helmet texture hash
        var shellRe = new System.Text.RegularExpressions.Regex(@"kitpart_assets/generic/helmets/([a-z0-9_]+)/idf/");
        foreach (var gender in new[] { "male", "female" })
        {
            var dir = Path.Combine(game.DriversDir, gender);
            if (!Directory.Exists(dir)) continue;
            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
            {
                var folder = Path.GetFileName(d);
                var m = System.Text.RegularExpressions.Regex.Match(folder, @"^(\d{4}|classic)-(.+?)_(.+)$");
                if (!m.Success) { Console.WriteLine($"?    {folder}"); continue; }
                var token = m.Groups[3].Value;
                var helmet = Path.Combine(d, token + "_helmet.erp");
                if (!File.Exists(helmet)) { Console.WriteLine($"{folder,-58} no helmet erp"); continue; }
                try
                {
                    var res = ErpX.Load(helmet).Resources;
                    var ids = res.Select(r => r.Identifier).ToList();
                    var shell = ids.Select(i => shellRe.Match(i)).Where(x => x.Success).Select(x => x.Groups[1].Value).FirstOrDefault() ?? "-";
                    var root = $"eaid://character_package/drivers/{gender}/{folder}/";
                    bool att = ids.Contains($"{root}idf/{token}_helmet_attachments.idf?lod=0&mesh");
                    bool ownEmb = ids.Contains($"{root}idf/{token}.emb?context={folder}");
                    bool ownShell = ids.Contains($"eaid://character_package/kitpart_assets/generic/helmets/{shell}/idf/{shell}.emb?context={folder}");
                    var d1 = res.FirstOrDefault(r => r.Identifier == $"{root}textures/{token}_helmet_d.tif");
                    string hash = "-";
                    if (d1 is not null)
                    {
                        var srv = new EgoEngineLibrary.Archive.Erp.Data.ErpGfxSRVResource(); srv.FromResource(d1);
                        var surf = res.First(r => r.Identifier == srv.Fragment0.SurfaceResourceName);
                        hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(ErpX.Data(surf, 1)))[..8];
                    }
                    Console.WriteLine($"{folder,-58} {shell,-26} att:{(att ? "y" : "n")} emb:{(ownEmb ? "y" : "n")} shellemb:{(ownShell ? "y" : "n")} helmet_d:{hash}");
                }
                catch (Exception ex) { Console.WriteLine($"{folder,-58} ERROR {ex.Message}"); }
            }
        }
        return 0;
    }
    if (args[0] == "preview")
    {
        // helmetswap preview --driver a [--from b] [--parts ...] --out file.png: front / side / back views
        Driver One(string opt) => drivers.Single(d => d.Folder == Opt(opt) || d.Token == Opt(opt));
        var target = One("--driver"); var donor = Opt("--from") is null ? null : One("--from");
        var parts = Opt("--parts") is { } ps
            ? ps.Split(',', StringSplitOptions.RemoveEmptyEntries).Aggregate((SwapParts)0, (acc, x) => acc | Enum.Parse<SwapParts>(x.Trim(), ignoreCase: true))
            : SwapParts.All;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (Opt("--page") is { } pagePath)
        {
            // helmetswap preview --driver a --from b [--parts ...] --page file.html: the interactive 3D page
            var html = MixOpt() is { } mixPicks
                ? HelmetPage.Page(game, target, mixPicks, mixPicks.Parts, int.Parse(Opt("--tex") ?? "2048"), embedded: args.Contains("--embedded"))
                : HelmetPage.Page(game, target, donor, parts, int.Parse(Opt("--tex") ?? "2048"), embedded: args.Contains("--embedded"));
            File.WriteAllText(pagePath, html);
            Console.WriteLine($"page: {html.Length / 1048576.0:0.0} MB in {sw.ElapsedMilliseconds} ms -> {pagePath}");
            return 0;
        }
        var scene = HelmetPreview.Scene(game, target, donor, parts);
        Console.WriteLine($"scene: {scene.Count} surfaces, {scene.Sum(m => m.Indices.Length / 3)} triangles ({string.Join(", ", scene.Select(m => m.Slot + (m.Texture is null ? "" : $" {m.Texture.Width}px")))}) in {sw.ElapsedMilliseconds} ms");
        int size = int.Parse(Opt("--size") ?? "360"); var yaws = (Opt("--yaws") ?? "25,90,180").Split(',').Select(float.Parse).ToArray();
        var sheet = new byte[size * yaws.Length * size * 4];
        for (int v = 0; v < yaws.Length; v++)
        {
            sw.Restart();
            var img = HelmetPreview.Render(scene, yaws[v], size, size, (246, 245, 250));
            Console.WriteLine($"view {yaws[v]}: {sw.ElapsedMilliseconds} ms");
            for (int y = 0; y < size; y++) Buffer.BlockCopy(img, y * size * 4, sheet, (y * size * yaws.Length + v * size) * 4, size * 4);
        }
        File.WriteAllBytes(Opt("--out") ?? "preview.png", HelmetPreview.Png(sheet, size * yaws.Length, size));
        return 0;
    }
    if (args[0] == "mats")
    {
        // helmetswap mats --driver token: every helmet slot's material (shader, values, textures) and vertex streams (analysis)
        var d = drivers.Single(x => x.Token == Opt("--driver") || x.Folder == Opt("--driver"));
        var idx = ErpX.Index(ErpX.Load(d.HelmetErp));
        if (File.Exists(HelmetSwapper.MainErp(d))) foreach (var r in ErpX.Load(HelmetSwapper.MainErp(d)).Resources) idx.TryAdd(r.Identifier, r);
        var common = ErpX.Index(ErpX.Load(HelmetSwapper.CommonHelmetErp(game)));
        EgoEngineLibrary.Archive.Erp.ErpResource? Find(string id) => idx.GetValueOrDefault(id) ?? common.GetValueOrDefault(id);
        bool Helmety(string k) => k.Contains("helmet") || k.Contains("visor") || k == "wing";
        foreach (var e in idx.Keys.Where(k => k.Contains(".emb")).Concat(common.Keys.Where(k => k.Contains(".emb") && k.Contains("helmet"))).Distinct())
        {
            Dictionary<string, string> b;
            try { b = EgoMaterial.ParseBinding(ErpX.Data(Find(e)!)); } catch { continue; }
            if (!b.Keys.Any(Helmety)) continue;
            Console.WriteLine($"binding {e.Split('/')[^1]}: {string.Join(", ", b.Where(kv => Helmety(kv.Key)).Select(kv => kv.Key + " -> " + kv.Value.Split('/')[^1]))}");
        }
        var mats = idx.Keys.Concat(common.Keys).Where(k => k.EndsWith(".material") && (k.Contains("helmet") || k.Contains("visor"))).Distinct();
        foreach (var mid in mats)
        {
            EgoMaterial m;
            try { m = EgoMaterial.Parse(ErpX.Data(Find(mid)!)); } catch { Console.WriteLine($"\n{mid}: unreadable"); continue; }
            Console.WriteLine($"\n{mid}\n  shader {m.ShaderName}");
            Console.WriteLine("  values " + string.Join("  ", m.Values.Select(kv => $"{kv.Key}={string.Join(",", kv.Value.Select(v => v.ToString("0.###")))}")));
            foreach (var (s, t) in m.Textures)
            {
                var tr = Find(t); string info = "(missing)";
                if (tr is not null) { try { var tex = TextureImage.Decode(game, tr, 4096); info = $"{tex.Width}x{tex.Height}"; } catch (Exception ex) { info = "undecodable " + ex.Message; } }
                Console.WriteLine($"  {s} = {t.Split('/')[^1]}  {info}");
            }
        }
        foreach (var root in new[] { HelmetSwapper.ShellRoot(d.Shell!), HelmetSwapper.Attachments(d) })
        {
            if (Find(root + "lod=0&mesh") is not { } mr) { Console.WriteLine($"\n{root}: no mesh"); continue; }
            var mesh = MeshDesc.Parse(ErpX.Data(mr));
            Console.WriteLine($"\n{root.Split('/')[^1]} slots {string.Join(",", mesh.Slots)}  streams of sub0 {string.Join(",", mesh.StreamsOf(0))}");
            for (int i = 0; i < mesh.Streams.Count; i++)
            {
                var sr = Find(mesh.Streams[i]);
                var h = sr is null ? [] : ErpX.Data(sr);
                Console.WriteLine($"  stream {i} fmt {Convert.ToHexString(mesh.Formats[i])} hdr {Convert.ToHexString(h.AsSpan(0, Math.Min(h.Length, 24)))} {mesh.Streams[i].Split('/')[^1]}");
            }
        }
        return 0;
    }
    if (args[0] == "wingparts")
    {
        // helmetswap wingparts [--all]: per driver, the parts-model triangles by slot, in / out of the wing zone, with the
        // bounds of the "wing"-slot triangles outside it (analysis: what else the wing surface holds)
        foreach (var d in drivers)
        {
            try
            {
                var idx = ErpX.Index(ErpX.Load(d.HelmetErp));
                if (File.Exists(HelmetSwapper.MainErp(d))) foreach (var r in ErpX.Load(HelmetSwapper.MainErp(d)).Resources) idx.TryAdd(r.Identifier, r);
                var root = HelmetSwapper.Attachments(d);
                if (!idx.TryGetValue(root + "lod=0&mesh", out var mr)) continue;
                var mesh = MeshDesc.Parse(ErpX.Data(mr));
                var ib = ErpX.Data(idx[mesh.IndexBuffer], 1);
                var parts = new List<string>();
                for (int s2 = 0; s2 < mesh.Records.Count; s2++)
                {
                    var pos = ErpX.Data(idx[mesh.Streams[mesh.StreamsOf(s2)[0]]], 1);
                    var (off, cnt) = mesh.IndexRange(s2); int bv = BitConverter.ToInt32(mesh.Records[s2], 20);
                    int inZ = 0, outZ = 0; var lo = new System.Numerics.Vector3(9); var hi = new System.Numerics.Vector3(-9);
                    for (int t = 0; t + 2 < cnt; t += 3)
                    {
                        var v = Enumerable.Range(0, 3).Select(k => MeshDesc.Position(pos, BitConverter.ToUInt16(ib, (off + t + k) * 2) + bv)).ToArray();
                        if (HelmetParts.InWingZone(v[0], v[1], v[2])) inZ++;
                        else { outZ++; foreach (var x in v) { lo = System.Numerics.Vector3.Min(lo, x); hi = System.Numerics.Vector3.Max(hi, x); } }
                    }
                    parts.Add($"{mesh.Slots[s2]} {inZ}/{outZ}" + (outZ > 0 && mesh.Slots[s2] == "wing" ? $" out y {lo.Y:0.00}..{hi.Y:0.00} z {lo.Z:0.00}..{hi.Z:0.00}" : ""));
                }
                Console.WriteLine($"{d.Year} {d.Series} {d.Name + (d.Variant is null ? "" : " (" + d.Variant + ")"),-34} {d.ShellName,-18} {string.Join(" | ", parts)}");
            }
            catch (Exception ex) { Console.WriteLine($"{d.Folder}: {ex.Message}"); }
        }
        return 0;
    }
    if (args[0] == "tangents")
    {
        // helmetswap tangents --driver token: how the normal / tangent streams relate to the UVs (analysis)
        var d = drivers.Single(x => x.Token == Opt("--driver") || x.Folder == Opt("--driver"));
        var idx = ErpX.Index(ErpX.Load(d.HelmetErp));
        if (File.Exists(HelmetSwapper.MainErp(d))) foreach (var r in ErpX.Load(HelmetSwapper.MainErp(d)).Resources) idx.TryAdd(r.Identifier, r);
        foreach (var root in new[] { HelmetSwapper.ShellRoot(d.Shell!), HelmetSwapper.Attachments(d) })
        {
            var mesh = MeshDesc.Parse(ErpX.Data(idx[root + "lod=0&mesh"]));
            var ib = ErpX.Data(idx[mesh.IndexBuffer], 1);
            for (int s = 0; s < mesh.Records.Count; s++)
            {
                var st = mesh.StreamsOf(s);
                byte[] P = ErpX.Data(idx[mesh.Streams[st[0]]], 1), N = ErpX.Data(idx[mesh.Streams[st[1]]], 1), T = ErpX.Data(idx[mesh.Streams[st[2]]], 1), UV = ErpX.Data(idx[mesh.Streams[st[3]]], 1);
                System.Numerics.Vector4 H4(byte[] b, int v) => new((float)BitConverter.ToHalf(b, v * 8), (float)BitConverter.ToHalf(b, v * 8 + 2), (float)BitConverter.ToHalf(b, v * 8 + 4), (float)BitConverter.ToHalf(b, v * 8 + 6));
                System.Numerics.Vector2 Uv(int v) => new((float)BitConverter.ToHalf(UV, v * 4), (float)BitConverter.ToHalf(UV, v * 4 + 2));
                var (off, cnt) = mesh.IndexRange(s); int bv = BitConverter.ToInt32(mesh.Records[s], 20);
                int n = 0, tu = 0, tuNeg = 0, bvPos = 0, bvNeg = 0; double nlen = 0, tlen = 0, ndt = 0; var ws = new Dictionary<string, int>(); var nws = new Dictionary<string, int>();
                for (int t = 0; t + 2 < cnt; t += 3)
                {
                    int a = BitConverter.ToUInt16(ib, (off + t) * 2) + bv, b = BitConverter.ToUInt16(ib, (off + t + 1) * 2) + bv, c = BitConverter.ToUInt16(ib, (off + t + 2) * 2) + bv;
                    var pa = MeshDesc.Position(P, a); var e1 = MeshDesc.Position(P, b) - pa; var e2 = MeshDesc.Position(P, c) - pa;
                    var d1 = Uv(b) - Uv(a); var d2 = Uv(c) - Uv(a);
                    float r = d1.X * d2.Y - d2.X * d1.Y; if (MathF.Abs(r) < 1e-10f) continue;
                    var dpdu = (e1 * d2.Y - e2 * d1.Y) / r; var dpdv = (e2 * d1.X - e1 * d2.X) / r;
                    var nn = H4(N, a); var tt = H4(T, a);
                    var n3 = new System.Numerics.Vector3(nn.X, nn.Y, nn.Z); var t3 = new System.Numerics.Vector3(tt.X, tt.Y, tt.Z);
                    n++; nlen += n3.Length(); tlen += t3.Length(); ndt += MathF.Abs(System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(n3), System.Numerics.Vector3.Normalize(t3)));
                    if (System.Numerics.Vector3.Dot(t3, dpdu) > 0) tu++; else tuNeg++;
                    var bit = System.Numerics.Vector3.Cross(n3, t3) * (tt.W < 0 ? -1 : 1);
                    if (System.Numerics.Vector3.Dot(bit, dpdv) > 0) bvPos++; else bvNeg++;
                    var wk = tt.W.ToString("0.##"); ws[wk] = ws.GetValueOrDefault(wk) + 1;
                    var nk = nn.W.ToString("0.#"); nws[nk] = nws.GetValueOrDefault(nk) + 1;
                }
                Console.WriteLine($"{root.Split('/')[^1]} {mesh.Slots[s]}: {n} tris  |n| {nlen / n:0.###} |t| {tlen / n:0.###} |n.t| {ndt / n:0.###}  t~+dP/du {tu} vs {tuNeg}  cross(n,t)*sign(w)~+dP/dv {bvPos} vs {bvNeg}");
                Console.WriteLine($"   tangent w: {string.Join(" ", ws.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key + ":" + x.Value))}   normal w: {string.Join(" ", nws.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key + ":" + x.Value))}");
            }
        }
        return 0;
    }
    if (args[0] == "shellcmp")
    {
        // helmetswap shellcmp --a file.erp --sa shell --b file.erp --sb shell: two helmet shells' LOD 0 compared (analysis)
        var ia = ErpX.Index(ErpX.Load(Path.Combine(game.Assets, Opt("--a")!)));
        var ib2 = ErpX.Index(ErpX.Load(Path.Combine(game.Assets, Opt("--b")!)));
        string ra = HelmetSwapper.ShellRoot(Opt("--sa")!), rb = HelmetSwapper.ShellRoot(Opt("--sb")!);
        var ma = MeshDesc.Parse(ErpX.Data(ia[ra + "lod=0&mesh"])); var mb = MeshDesc.Parse(ErpX.Data(ib2[rb + "lod=0&mesh"]));
        Console.WriteLine($"slots {string.Join(",", ma.Slots)} | {string.Join(",", mb.Slots)}");
        for (int s2 = 0; s2 < Math.Min(ma.Records.Count, mb.Records.Count); s2++)
        {
            var (oa, ca) = ma.IndexRange(s2); var (ob, cb) = mb.IndexRange(s2);
            Console.WriteLine($"sub {s2} {ma.Slots[s2]}: indices {ca} vs {cb}, base {BitConverter.ToInt32(ma.Records[s2], 20)} vs {BitConverter.ToInt32(mb.Records[s2], 20)}");
        }
        for (int k = 0; k < 6; k++)
        {
            byte[] da = ErpX.Data(ia[ma.Streams[ma.StreamsOf(0)[k]]], 1), db = ErpX.Data(ib2[mb.Streams[mb.StreamsOf(0)[k]]], 1);
            int same = 0, n = Math.Min(da.Length, db.Length);
            for (int i = 0; i < n; i++) if (da[i] == db[i]) same++;
            Console.WriteLine($"stream {k}: {da.Length} vs {db.Length} bytes, identical bytes {100.0 * same / Math.Max(1, n):0.0}%  equal {da.AsSpan().SequenceEqual(db)}");
        }
        byte[] ia0 = ErpX.Data(ia[ma.IndexBuffer], 1), ib0 = ErpX.Data(ib2[mb.IndexBuffer], 1);
        Console.WriteLine($"index buffer: {ia0.Length} vs {ib0.Length}, equal {ia0.AsSpan().SequenceEqual(ib0)}");
        return 0;
    }
    if (args[0] == "export")
    {
        // helmetswap export --driver a [--from b] [--parts ...] --out DIR: the helmet as FBX + OBJ with textures (Painter / Blender)
        Driver One(string opt) => drivers.Single(d => d.Folder == Opt(opt) || d.Token == Opt(opt));
        var target = One("--driver"); var donor = Opt("--from") is null ? null : One("--from");
        var parts = Opt("--parts") is { } ps
            ? ps.Split(',', StringSplitOptions.RemoveEmptyEntries).Aggregate((SwapParts)0, (acc, x) => acc | Enum.Parse<SwapParts>(x.Trim(), ignoreCase: true))
            : SwapParts.All;
        var log = new SwapLog(); log.Line += (s, _) => Console.WriteLine("  " + s);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var picks = MixOpt() ?? (donor is null ? null : PartPicks.Of(donor, parts));
        var files = HelmetExport.Write(game, target, picks, Opt("--out") ?? throw new ArgumentException("--out DIR required"), log);
        Console.WriteLine($"{files.Count} files in {sw.ElapsedMilliseconds} ms");
        return 0;
    }
    if (args[0] == "slots")
    {
        // helmetswap slots [--all] [--career]: each helmet's parts-model surfaces (analysis: who has no visor strip / wing)
        foreach (var d in sel)
            try
            {
                var idx = ErpX.Index(ErpX.Load(d.HelmetErp));
                var slots = HelmetSwapper.MeshSlots(ErpX.Data(idx[HelmetSwapper.Attachments(d) + "lod=0&mesh"]));
                Console.WriteLine($"{(slots.Contains("visor_attachments") ? "strip   " : "NO STRIP")} {d.Folder,-50} {string.Join(",", slots)}");
            }
            catch (Exception ex) { Console.WriteLine($"?        {d.Folder,-50} {ex.Message}"); }
        return 0;
    }
    if (args[0] == "texpng")
    {
        // helmetswap texpng --drivers a --match text --out DIR [--size 1024]: matching textures as PNGs (analysis)
        var match = Opt("--match") ?? throw new ArgumentException("--match required");
        var outDir = Opt("--out") ?? throw new ArgumentException("--out DIR required");
        int size = int.Parse(Opt("--size") ?? "1024");
        Directory.CreateDirectory(outDir);
        var sources = Opt("--file") is { } one ? [("file", Path.Combine(game.Assets, one))] : sel.Select(d => (d.Token, d.HelmetErp));
        foreach (var (tag, path) in sources)
            foreach (var r in ErpX.Load(path).Resources.Where(r => r.ResourceType == "GfxSRVResource" && r.Identifier.Contains(match, StringComparison.Ordinal)))
            {
                var img = TextureImage.Decode(game, r, size);
                var file = Path.Combine(outDir, $"{tag}__{r.Identifier.Split('/')[^1]}.png");
                File.WriteAllBytes(file, img.ToPng());
                Console.WriteLine($"{img.Width}x{img.Height}  {file}");
            }
        return 0;
    }
    if (args[0] == "meshcheck")
    {
        // every helmet mesh (attachments + shells, all LODs) parses and writes back byte-identical
        int ok = 0, bad = 0, shared = 0;
        foreach (var d in drivers)
            foreach (var r in ErpX.Load(d.HelmetErp).Resources.Where(r => r.ResourceType == "GfxMeshRes"))
            {
                var data = ErpX.Data(r);
                try
                {
                    var m = MeshDesc.Parse(data);
                    if (m.SharedNames) { shared++; continue; }      // the My Team parts model: read, never rebuilt
                    if (m.Write().AsSpan().SequenceEqual(data)) ok++;
                    else { bad++; Console.WriteLine("DIFF  " + r.Identifier); }
                }
                catch (Exception ex) { bad++; Console.WriteLine($"FAIL  {r.Identifier}: {ex.Message}"); }
            }
        Console.WriteLine($"{ok} meshes round-trip, {bad} don't" + (shared > 0 ? $" ({shared} shared-name meshes read only)" : ""));
        return bad == 0 ? 0 : 2;
    }
    if (args[0] == "raw")
    {
        // helmetswap raw --drivers a --match text --out DIR: every matching resource's fragments as files (for analysis)
        var match = Opt("--match") ?? throw new ArgumentException("--match required");
        var outDir = Opt("--out") ?? throw new ArgumentException("--out DIR required");
        Directory.CreateDirectory(outDir);
        var sources = Opt("--file") is { } one ? [("file", Path.Combine(game.Assets, one))] : sel.Select(d => (d.Token, d.HelmetErp));
        foreach (var (tag, path) in sources)
            foreach (var r in ErpX.Load(path).Resources.Where(r => r.Identifier.Contains(match, StringComparison.Ordinal)))
                for (int f = 0; f < r.Fragments.Count; f++)
                {
                    var name = $"{tag}__{System.Text.RegularExpressions.Regex.Replace(r.Identifier.Split('/')[^1], "[^a-z0-9_.=-]", "_")}.f{f}.bin";
                    File.WriteAllBytes(Path.Combine(outDir, name), ErpX.Data(r, f));
                }
        return 0;
    }
    if (args[0] == "swap")
    {
        Driver Pick(string opt) => (drivers.Where(d => d.Folder == Opt(opt) || d.Token == Opt(opt)).ToList() is [var exact] ? [exact] : drivers.Where(d => d.Token.Contains(Opt(opt) ?? throw new ArgumentException(opt + " required"), StringComparison.OrdinalIgnoreCase)
                                                     || d.Name.Contains(Opt(opt)!, StringComparison.OrdinalIgnoreCase)).ToList()) switch
        {
            [var one] => one,
            [] => throw new ArgumentException($"no driver matches '{Opt(opt)}'"),
            var many => throw new ArgumentException($"'{Opt(opt)}' matches {string.Join(", ", many.Select(m => m.Name))}"),
        };
        Driver Find(string key) => (drivers.Where(d => d.Folder == key || d.Token == key).ToList() is [var exact] ? [exact]
            : drivers.Where(d => d.Token.Contains(key, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList()) switch
        {
            [var one] => one,
            [] => throw new ArgumentException($"no driver matches '{key}'"),
            var many => throw new ArgumentException($"'{key}' matches {string.Join(", ", many.Select(m => m.Name))}"),
        };
        var target = Pick("--driver");
        if (Opt("--hide") is { } hide) HelmetSwapper.HideWith = hide is "logo" or "tear" ? hide : throw new ArgumentException("--hide logo|tear");
        var outDir = Opt("--out") ?? throw new ArgumentException("--out DIR required");
        Directory.CreateDirectory(outDir);
        var log = new SwapLog();
        PartPicks picks;
        if (Opt("--mix") is { } mix)
        {
            // --mix design:max_verstappen,visor:2024-f2-camposracing_isack_hadjar,wing:nico_hulkenberg  (parts from several helmets)
            picks = new PartPicks();
            foreach (var pair in mix.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (part, who) = (pair[..pair.IndexOf(':')], pair[(pair.IndexOf(':') + 1)..]);
                picks[Enum.Parse<SwapParts>(part.Trim(), ignoreCase: true)] = Find(who.Trim());
            }
        }
        else
        {
            // --from driver [--parts design,visor,wing] (default: the whole helmet)
            var parts = Opt("--parts") is { } ps
                ? ps.Split(',', StringSplitOptions.RemoveEmptyEntries).Aggregate((SwapParts)0, (acc, x) => acc | Enum.Parse<SwapParts>(x.Trim(), ignoreCase: true))
                : SwapParts.All;
            picks = PartPicks.Of(Pick("--from"), parts);
        }
        var path = Path.Combine(outDir, HelmetSwapper.OutputName(target, picks));
        log.Line += (s, _) => Console.WriteLine("  " + s);
        var checks = HelmetSwapper.BuildAndSave(game, target, picks, path, log);
        Console.WriteLine($"{string.Join(" + ", picks.Donors.Select(d => d.Name))} -> {target.Name}: {new FileInfo(path).Length / (1024 * 1024.0):F1} MB, {checks} checks passed -> {Path.GetFileName(path)}");
        return 0;
    }
    Console.Error.WriteLine("unknown command " + args[0]);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}
