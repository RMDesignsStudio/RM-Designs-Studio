using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using WheelSwap.Core;

namespace Swapper.Core;

/// <summary>What a rim swap did, for the log and the checks.</summary>
public sealed class RimSwapResult
{
    public required string Donor { get; init; }
    public string ModelToken { get; set; } = "";
    public bool PrivateCopy { get; set; }
    public List<int> Lods { get; } = [];
    public List<int> KeptLods { get; } = [];
    public List<string> Textures { get; } = [];
}

/// <summary>
/// Rims (the cars' road wheels): another car's rims on a car. Every car has the same wheel rig: four <c>wheel_xx</c>
/// nodes at its axles, each rim stored around its own node (radius 0.248 m, half width 0.167 m front, 0.21 m rear, on
/// every car) and drawn by the <c>wheel</c> material slot, plus the hub centre pieces on the four <c>disc_xx</c> nodes. The
/// tyres are a shared model the game puts on the wheel nodes, and the uprights / calipers are the car's own: neither changes.
/// So, as the steering wheel graft does: the donor's rim and disc pieces are appended to the car's vertex and index
/// buffers (bones remapped by node name, so they sit on the car's own axles), the car's own are hidden, every rim submesh
/// is pointed at the new geometry, and the car's rim textures take the donor's images (its UVs come with its geometry).
/// A detail level is swapped where both cars draw the wheels with the same slots; the far ones that paint the wheels with
/// the body livery (or draw them inside the whole car's shadow) keep the car's own.
/// The car's model is edited where it is when the ERP carries it (a swapped chassis, a wheel build's private model);
/// otherwise a private copy is made (a same-length name, never one the wheel engine reads as its own earlier build).
/// </summary>
public static class RimSwap
{
    static readonly string[] Corners = ["fl", "fr", "bl", "br"];
    public static readonly string[] Family = [.. Corners.Select(c => "wheel_" + c), .. Corners.Select(c => "disc_" + c)];

    /// <summary>In-game diagnosis only (the command line's --rims-test): build a part of the swap to find what the game
    /// refuses. Off in the app.</summary>
    public static class Test
    {
        /// <summary>Only the rim textures; the car's own geometry.</summary>
        public static bool TexturesOnly;
        /// <summary>No 'disc' pieces (the brake discs keep the car's own).</summary>
        public static bool NoDiscs;
        /// <summary>Detail levels above this keep the car's own (6 = all).</summary>
        public static int MaxLod = 6;
    }

    /// <summary>"Ferrari Rims", "Racing Bulls Suzuka 26 Rims": the ending a section with new rims gets.</summary>
    public static string Suffix(ChassisDonor donor) => $"{donor.Label.Replace(" · ", " ")} Rims";

    /// <summary>Put <paramref name="donor"/>'s rims on the car of <paramref name="entryBytes"/> (a chassis option's ERP);
    /// <paramref name="others"/> are the template's other ERPs, for the parts of the car they carry. <paramref name="myTeam"/>:
    /// the option is a My Team livery (a binding and textures only, worn by the game's FOM car).</summary>
    public static (byte[] Erp, RimSwapResult Result) Run(GameData game, byte[] entryBytes, int season, ChassisDonor donor, IEnumerable<ErpFile> others, SwapLog log, bool myTeam = false)
    {
        if (donor.Season != season) throw new InvalidOperationException($"{donor.Label} is a {donor.Season} car: rims go on cars of their own season ({season}).");
        var result = new RimSwapResult { Donor = donor.Label };
        var entry = ErpX.Load(entryBytes);
        bool IsModelLayer(ErpResource r) => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal) || r.Identifier.EndsWith("-carshowroommodel.layer", StringComparison.Ordinal);
        if (myTeam && !entry.Resources.Any(IsModelLayer))
        {
            // a My Team livery carries no car: the game spawns the FOM car from the fom_car slot (and the myteam_<engine>
            // slots) and dresses it with the livery's binding. Those slots' car-model layers come along, so they can point at
            // the car with the new rims (as the steering wheel swap does)
            foreach (var slot in GameData.MyTeamSlots)
            {
                var path = game.TeamErp(slot, season);
                if (!File.Exists(path)) continue;
                foreach (var r in ErpX.Load(path).Resources.Where(IsModelLayer)) ErpX.AddOrReplace(entry, r);
            }
        }
        var layers = entry.Resources.Where(IsModelLayer).ToList();
        var main = layers.FirstOrDefault(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal)) ?? throw new InvalidDataException("the chassis has no car-model layer");
        var m = System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.ASCII.GetString(ErpX.Data(main)), @"/idf/([A-Za-z0-9_]+)\.idf\?context=render");
        if (!m.Success) throw new InvalidDataException("the car-model layer names no model");
        var token = m.Groups[1].Value;
        var bindings = ErpX.Eaids(ErpX.Data(main)).Where(e => e.Contains(".emb?context=", StringComparison.Ordinal)).Distinct().ToList();
        // a My Team livery's own binding (fom_car.emb?context=livery_...) names its rim material
        if (myTeam) bindings.InsertRange(0, entry.Resources.Where(r => r.Identifier.Contains(".emb?context=", StringComparison.Ordinal)).Select(r => r.Identifier));
        var otherList = others.ToList();
        var target = new CarModel(game, token, [entry], season, otherList);
        var donorErp = ErpX.Load(donor.ErpPath);
        var donorModel = new CarModel(game, Catalog.ModelTokenOf(donor), [donorErp], donor.Season);
        result.ModelToken = token;

        // is the model the ERP's own (edited where it is), or the game's / another ERP's (a private copy)?
        var entryIds = entry.Resources.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal);
        bool owned = target.Closure().All(r => entryIds.Contains(r.Identifier));

        if (!Test.TexturesOnly)
        {
            Graft(target, donorModel, result, log);
            if (result.Lods.Count == 0) throw new InvalidDataException($"{donor.Label}'s rims don't match this car's wheels at any detail level.");
            target.Commit();
        }
        else log.Warn("Rims test: textures only, the car keeps its own rim geometry.");

        if (!owned && !Test.TexturesOnly)
        {
            // every model resource the car uses, renamed per namespace to a same-length private name and carried by the ERP;
            // the car-model layers point at it
            var closure = target.Closure();
            var names = CarModel.Namespaces(closure).ToDictionary(n => n, n => PrivateName(n, donor.Id));
            foreach (var r in closure)
            {
                bool changed = false;
                foreach (var f in r.Fragments)
                {
                    var data = f.GetDataArray(true);
                    int hits = 0;
                    foreach (var (n, p) in names) hits += ErpX.ReplaceInEaids(data, n + ".idf", p + ".idf");
                    if (hits > 0) { f.SetData(data, true); changed = true; }
                }
                foreach (var (n, p) in names) r.Identifier = r.Identifier.Replace($"/idf/{n}.idf", $"/idf/{p}.idf", StringComparison.Ordinal);
                if (changed) ErpX.Rehash(r);
                ErpX.AddOrReplace(entry, r);
            }
            foreach (var l in layers)
            {
                var d = ErpX.Data(l);
                if (ErpX.ReplaceBytes(d, $"/idf/{token}.idf?context=render", $"/idf/{names[token]}.idf?context=render") == 0) continue;
                l.Fragments[0].SetData(d, true);
                ErpX.Rehash(l);
            }
            result.ModelToken = names[token];
            result.PrivateCopy = true;
            log.Info($"Rims: a private copy of the car's model, {string.Join(", ", names.Select(kv => $"{kv.Key} -> {kv.Value}"))} ({closure.Count} resources).");
        }

        // the rim textures: the car's wheel material's, with the donor's images
        var finder = new Finder(entry, target, otherList, game, season);
        var targetTex = RimTextures(bindings, finder);
        var donorLayer = donorErp.Resources.First(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal));
        var donorFinder = new Finder(donorErp, donorModel, [], game, donor.Season);
        var donorTex = RimTextures(ErpX.Eaids(ErpX.Data(donorLayer)).Where(e => e.Contains(".emb?context=", StringComparison.Ordinal)).Distinct(), donorFinder);
        foreach (var (ch, tid) in targetTex)
        {
            if (!donorTex.TryGetValue(ch, out var did)) { log.Warn($"Rims: {donor.Label} has no '{ch}' rim texture; the car's is kept."); continue; }
            var dres = donorFinder.Find(did) ?? throw new InvalidDataException($"{did} not found");
            var dsrv = new ErpGfxSRVResource();
            dsrv.FromResource(dres);
            EgoEngineLibrary.Graphics.Dds.DdsFile dds;
            var mips = dsrv.SurfaceRes.HasMips ? TeamSimplified.ChassisSwap.OwnMipmaps(game, dsrv) : null;
            using (Stream? s = mips is null ? null : File.OpenRead(mips)) dds = dsrv.ToDdsFile(s, false, 0);
            var srv = InEntry(entry, tid, finder);
            TextureSwap.Apply(entry, srv, dds);
            result.Textures.Add(tid);
            log.Info($"Rims: texture {tid.Split('/')[^1]} <- {did.Split('/')[^1]} ({dds.header.width}x{dds.header.height})");
        }
        if (result.Textures.Count == 0) throw new InvalidDataException("the car's rim textures could not be found");

        log.Info($"Rims from {donor.Label}: detail levels {string.Join(", ", result.Lods)} swapped" +
                 (result.KeptLods.Count > 0 ? $"; {string.Join(", ", result.KeptLods)} keep the car's own (drawn with its body or shadow there)" : "") + ".");
        return (ErpX.Save(entry), result);
    }

    /// <summary>
    /// The finished ERP read back and checked: it loads; the car-model layer's model is complete; every swapped detail level's
    /// rim submeshes draw the donor's vertex count; the rim textures carry the donor's images. Returns the number of checks.
    /// </summary>
    public static int Verify(GameData game, byte[] erpBytes, int season, RimSwapResult result, IEnumerable<ErpFile> others)
    {
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) throw new InvalidDataException($"Rim check failed: {what}"); }
        ErpFile erp;
        try { erp = ErpX.Load(erpBytes); }
        catch (InvalidDataException ex) { throw new InvalidDataException("Rim check failed: the chassis can't be read back: " + ex.Message, ex); }
        var layer = erp.Resources.First(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal));
        Check(System.Text.Encoding.ASCII.GetString(ErpX.Data(layer)).Contains($"/idf/{result.ModelToken}.idf?context=render"), "the car-model layer doesn't point at the car with the new rims");
        CarModel model;
        try { model = new CarModel(game, result.ModelToken, [erp], season, others); }
        catch (InvalidDataException ex) { throw new InvalidDataException("Rim check failed: " + ex.Message, ex); }
        Check(true, "the model loads");
        foreach (var lod in result.Lods)
            foreach (var part in Family)
            {
                var pm = model.TryMesh($"?lod={lod}&{part}&mesh");
                if (pm is null || pm.Empty) continue;
                foreach (var s in pm.Subs.Where(s => s.ICount > 0))
                    Check(model.Res.ContainsKey(pm.IndexBuffer) && s.IOff + s.ICount <= model.Buf(pm.IndexBuffer).Count, $"LOD{lod} {part} '{s.Name}' draws outside the index buffer");
                // the rims keep the whole wheel's bounding sphere (tyre included, about 0.33 m on every car); a tight one
                // around the rim alone sits the car lower on track
                var md = model.Frags(model.MeshId($"?lod={lod}&{part}&mesh"))[0];
                foreach (var s in pm.Subs.Where(s => s.ICount > 0 && s.Name == "wheel"))
                    Check(BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(76 + s.Index * 88 + 36)) > 0.3f, $"LOD{lod} {part}'s rim lost the wheel's size");
            }
        var ids = erp.Resources.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal);
        foreach (var t in result.Textures)
        {
            Check(ids.Contains(t), $"{t.Split('/')[^1]} is not in the chassis");
            var srv = new ErpGfxSRVResource();
            srv.FromResource(erp.Resources.First(r => r.Identifier == t));
            Check(!srv.SurfaceRes.HasMips, $"{t.Split('/')[^1]} still needs an external .mipmaps file");
        }
        return checks;
    }

    // ------------------------------------------------------------------ geometry

    static void Graft(CarModel target, CarModel donor, RimSwapResult result, SwapLog log)
    {
        var tn = NodeTable.Parse(target.Render); var dn = NodeTable.Parse(donor.Render);
        // the rims are stored around their nodes; both cars' nodes must turn the same way for them to fit
        foreach (var part in Family)
        {
            var a = tn.Nodes.FirstOrDefault(n => n.Name == part); var b = dn.Nodes.FirstOrDefault(n => n.Name == part);
            if (a is null || b is null) throw new InvalidDataException($"{(a is null ? "the car" : "the donor")} has no '{part}' node");
            var qa = new Quaternion(a.Q[0], a.Q[1], a.Q[2], a.Q[3]); var qb = new Quaternion(b.Q[0], b.Q[1], b.Q[2], b.Q[3]);
            if (MathF.Abs(Quaternion.Dot(qa, qb)) < 0.9999f || MathF.Abs(a.S[0] - b.S[0]) + MathF.Abs(a.S[1] - b.S[1]) + MathF.Abs(a.S[2] - b.S[2]) > 1e-3f)
                throw new InvalidDataException($"the donor's '{part}' node is turned differently from the car's");
        }
        var boneMap = new Dictionary<int, byte>();
        byte MapBone(int b)
        {
            if (boneMap.TryGetValue(b, out var x)) return x;
            var name = dn.Nodes[dn.Palette[b]].Name;
            var t = tn.Nodes.FirstOrDefault(n => n.Name == name) ?? throw new InvalidDataException($"the donor's rims are bound to '{name}', which the car hasn't");
            int ti = tn.PaletteIndexOf(t.Index);
            if (ti < 0 || ti > 255) throw new InvalidDataException($"the car has no bone palette entry for '{name}'");
            return boneMap[b] = (byte)ti;
        }

        var ibId = target.Mesh("?lod=0&mesh").IndexBuffer;
        int ibStride = target.Buf(ibId).Stride;
        var ibOrig = ReadIndices(target, ibId);
        var ibExtra = new List<uint>();
        var pending = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int Count(string e) => counts.TryGetValue(e, out var c) ? c : (counts[e] = target.Buf(e).Count);
        var donorIdx = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        // per swapped detail level: each slot's new block, and each part's slice of it
        var slotBlocks = new Dictionary<(int Lod, string Slot), (int IOff, int ICount, int VOff, Vector4 Sphere)>();
        var partBlocks = new Dictionary<(int Lod, string Part, string Slot), (int IOff, int ICount, int VOff, Vector4 Sphere)>();
        var hide = new List<(int A, int B)>();

        var mergedPatch = new List<(int Lod, int Sub, string Slot)>();
        for (int lod = 0; lod < 7; lod++)
        {
            var merged = target.TryMesh($"?lod={lod}&mesh");
            if (merged is null || merged.Empty) continue;
            if (lod > Test.MaxLod) { result.KeptLods.Add(lod); log.Warn($"Rims test: LOD{lod} keeps the car's own."); continue; }
            var tparts = Family.ToDictionary(p => p, p => target.TryMesh($"?lod={lod}&{p}&mesh") is { Empty: false } x ? x : null);
            if (tparts.Values.All(v => v is null)) continue;
            var famRanges = tparts.Values.Where(v => v is not null).SelectMany(v => v!.Subs.Where(s => s.ICount > 0).Select(s => (A: s.IOff, B: s.IOff + s.ICount))).ToList();
            var mergedSubs = merged.Subs.Where(s => s.ICount > 0 && famRanges.Any(r => r.A < s.IOff + s.ICount && r.B > s.IOff)).ToList();
            bool swapped = false;
            foreach (var slot in mergedSubs.Select(s => s.Name).Distinct())
            {
                // painted with the body livery, or the whole car's shadow: those don't use the rim texture, so they stay
                if (Test.NoDiscs && slot == "disc") { log.Warn($"Rims test: LOD{lod} keeps the car's own brake discs."); continue; }
                if (slot is "body" or "shadow") { log.Info($"Rims LOD{lod}: the car's '{slot}' wheels are kept (drawn with the car's {(slot == "body" ? "livery" : "shadow")})."); continue; }
                var withSlot = Family.Where(p => tparts[p] is { } tm && tm.Subs.Any(s => s.ICount > 0 && s.Name == slot)).ToList();
                var slotRanges = withSlot.SelectMany(p => tparts[p]!.Subs.Where(s => s.ICount > 0 && s.Name == slot).Select(s => (A: s.IOff, B: s.IOff + s.ICount))).ToList();
                var msubs = mergedSubs.Where(s => s.Name == slot).ToList();
                // pieces of other parts the whole car's rim piece also draws (McLaren's LOD1 draws two small wing / cockpit
                // pieces with the rim material): they come along into the new block as the car has them
                var gaps = new List<(SubMesh Ms, int A, int B)>();
                foreach (var ms in msubs)
                {
                    var cov = new bool[ms.ICount];
                    foreach (var (a, b) in slotRanges) for (int i = Math.Max(a, ms.IOff); i < Math.Min(b, ms.IOff + ms.ICount); i++) cov[i - ms.IOff] = true;
                    for (int i = 0; i < ms.ICount; i++)
                    {
                        if (cov[i]) continue;
                        int j = i;
                        while (j < ms.ICount && !cov[j]) j++;
                        gaps.Add((ms, ms.IOff + i, ms.IOff + j));
                        i = j;
                    }
                }
                bool covered = msubs.Count > 0 && msubs.Select(ms => merged.StreamEaids(ms)).All(st => st.SequenceEqual(merged.StreamEaids(msubs[0])));
                // the donor's pieces of this slot: at this detail level, else its next more detailed one
                int k = lod;
                for (; k >= 0; k--)
                    if (withSlot.All(p => donor.TryMesh($"?lod={k}&{p}&mesh") is { Empty: false } dm && dm.Subs.Any(s => s.ICount > 0 && s.Name == slot))) break;
                if (!covered || k < 0)
                {
                    var why = !covered ? "its pieces are laid out differently" : $"{donor.Token} has no '{slot}' piece for every wheel";
                    if (slot == "wheel") log.Warn($"Rims LOD{lod}: the car keeps its own rims ({why}); far away they show the new rim texture on the old rims.");
                    else log.Info($"Rims LOD{lod}: the car keeps its own '{slot}' pieces ({why}).");
                    continue;
                }
                if (k != lod) log.Info($"Rims LOD{lod} '{slot}': {donor.Token} has no such pieces at this detail level, so its LOD{k} pieces are used.");
                var tsub = msubs[0];
                var tstreams = merged.StreamEaids(tsub);
                int baseV = Count(tstreams[0]);
                if (tstreams.Any(e => Count(e) != baseV)) throw new InvalidDataException($"LOD{lod} '{slot}': the car's vertex streams have different counts");
                var chunks = tstreams.Select(_ => new List<byte[]>()).ToArray();
                var newIdx = new List<uint>();
                int nverts = 0, ioff0 = ibOrig.Length + ibExtra.Count;
                foreach (var p in withSlot)
                {
                    var dm = donor.Mesh($"?lod={k}&{p}&mesh");
                    int partStart = newIdx.Count;
                    var partPos = new List<float>();
                    foreach (var s in dm.Subs.Where(s => s.ICount > 0 && s.Name == slot))
                    {
                        var plan = StreamPlan(donor, target, dm, s, tstreams) ?? throw new InvalidDataException($"LOD{lod} {p} '{slot}': the donor's vertex layout differs from the car's");
                        var sstreams = dm.StreamEaids(s);
                        if (!donorIdx.TryGetValue(dm.IndexBuffer, out var dib)) donorIdx[dm.IndexBuffer] = dib = ReadIndices(donor, dm.IndexBuffer);
                        var global = new int[s.ICount];
                        for (int i = 0; i < s.ICount; i++) global[i] = (int)dib[s.IOff + i] + s.VOff;
                        var uniq = global.Distinct().Order().ToArray();
                        var remap = new Dictionary<int, int>(uniq.Length);
                        for (int i = 0; i < uniq.Length; i++) remap[uniq[i]] = i;
                        for (int c = 0; c < tstreams.Length; c++)
                        {
                            if (plan[c] < 0)
                            {
                                var tb = target.Buf(tstreams[c]);
                                var fill = target.Frags(tstreams[c])[1].AsSpan(((int)ibOrig[tsub.IOff] + tsub.VOff) * tb.Stride, tb.Stride).ToArray();
                                var filled = new byte[uniq.Length * tb.Stride];
                                for (int i = 0; i < uniq.Length; i++) fill.CopyTo(filled, i * tb.Stride);
                                chunks[c].Add(filled);
                                continue;
                            }
                            var sb = donor.Buf(sstreams[plan[c]]);
                            var src = donor.Frags(sstreams[plan[c]])[1];
                            var rows = new byte[uniq.Length * sb.Stride];
                            for (int i = 0; i < uniq.Length; i++) Buffer.BlockCopy(src, uniq[i] * sb.Stride, rows, i * sb.Stride, sb.Stride);
                            if (sb.Fvf is BufInfo.FvfBones or BufInfo.FvfBones8)
                                for (int i = 0; i < uniq.Length; i++) rows[i * sb.Stride] = MapBone(rows[i * sb.Stride]);
                            if (sb.Fvf == BufInfo.FvfPosition && sb.Stride == 12) partPos.AddRange(MemoryMarshal.Cast<byte, float>(rows).ToArray());
                            chunks[c].Add(rows);
                        }
                        foreach (var g in global) newIdx.Add((uint)(remap[g] + nverts));
                        nverts += uniq.Length;
                    }
                    partBlocks[(lod, p, slot)] = (ioff0 + partStart, newIdx.Count - partStart, baseV, Sphere(partPos));
                }
                foreach (var (ms, a, b) in gaps)
                {
                    var global = new int[b - a];
                    for (int i = a; i < b; i++) global[i - a] = (int)ibOrig[i] + ms.VOff;
                    var uniq = global.Distinct().Order().ToArray();
                    var remap = new Dictionary<int, int>(uniq.Length);
                    for (int i = 0; i < uniq.Length; i++) remap[uniq[i]] = i;
                    for (int c = 0; c < tstreams.Length; c++)
                    {
                        var tb = target.Buf(tstreams[c]);
                        var src = target.Frags(tstreams[c])[1];
                        var rows = new byte[uniq.Length * tb.Stride];
                        for (int i = 0; i < uniq.Length; i++) Buffer.BlockCopy(src, uniq[i] * tb.Stride, rows, i * tb.Stride, tb.Stride);
                        chunks[c].Add(rows);
                    }
                    foreach (var g in global) newIdx.Add((uint)(remap[g] + nverts));
                    nverts += uniq.Length;
                }
                if (gaps.Count > 0) log.Info($"Rims LOD{lod}: {gaps.Sum(g => g.B - g.A) / 3} triangles of other parts drawn with the rim material come along as the car has them.");
                if (ibStride == 2 && nverts > 65536) throw new InvalidDataException($"LOD{lod} '{slot}': too many vertices for 16-bit indices");
                for (int c = 0; c < tstreams.Length; c++)
                {
                    if (!pending.TryGetValue(tstreams[c], out var pl)) pending[tstreams[c]] = pl = [];
                    pl.AddRange(chunks[c]); counts[tstreams[c]] = baseV + nverts;
                }
                ibExtra.AddRange(newIdx);
                slotBlocks[(lod, slot)] = (ioff0, newIdx.Count, baseV, default);
                hide.AddRange(slotRanges);
                foreach (var ms in msubs) mergedPatch.Add((lod, ms.Index, slot));
                swapped = true;
            }
            if (swapped) result.Lods.Add(lod); else result.KeptLods.Add(lod);
        }

        // hide the car's own rims, append the new indices / vertices
        foreach (var (a, b) in hide) Array.Clear(ibOrig, a, b - a);
        var ibNew = new uint[ibOrig.Length + ibExtra.Count];
        ibOrig.CopyTo(ibNew, 0); ibExtra.CopyTo(ibNew, ibOrig.Length);
        target.Set(ibId, 1, WriteIndices(ibNew, ibStride));
        SetCount(target, ibId, ibNew.Length);
        foreach (var (e, chunks) in pending)
        {
            var old = target.Frags(e)[1];
            var all = new byte[old.Length + chunks.Sum(c => c.Length)];
            old.CopyTo(all, 0); int at = old.Length;
            foreach (var c in chunks) { c.CopyTo(all, at); at += c.Length; }
            target.Set(e, 1, all);
            SetCount(target, e, counts[e]);
            if (all.Length != counts[e] * target.Buf(e).Stride) throw new InvalidDataException("vertex buffer size mismatch");
        }

        // point the rim submeshes at the new geometry: each part's own, and the whole car's (which keeps its bounding sphere)
        foreach (var lod in result.Lods)
        {
            foreach (var p in Family)
            {
                var q = $"?lod={lod}&{p}&mesh";
                if (!target.Has(q)) continue;
                var d = target.Frags(target.MeshId(q))[0];
                var pm = MeshDesc.Parse(d);
                if (pm.Empty) continue;
                var done = new HashSet<string>();
                bool touched = false;
                foreach (var s in pm.Subs.Where(s => s.ICount > 0 && partBlocks.ContainsKey((lod, p, s.Name))))
                {
                    var bl = partBlocks[(lod, p, s.Name)];
                    // the car's own bounding sphere is kept (about 0.33 m on every car, the same on all four corners: the
                    // whole wheel's, which the game also goes by on track; a tight one around the new rim sat the car too low)
                    int rb = 76 + s.Index * 88;
                    if (done.Add(s.Name)) MeshDesc.PatchRecord(d, s.Index, bl.IOff, bl.ICount, bl.VOff,
                        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(rb + 24)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(rb + 28)),
                        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(rb + 32)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(rb + 36)));
                    else MeshDesc.PatchRecord(d, s.Index, 0, 0, 0, 0, 0, 0, 0);   // a second piece of one slot: drawn by the first
                    touched = true;
                }
                if (touched) target.Touch(target.MeshId(q));
            }
        }
        foreach (var lod in mergedPatch.Select(x => x.Lod).Distinct())
        {
            var md = target.Frags(target.MeshId($"?lod={lod}&mesh"))[0];
            var done = new HashSet<string>();
            foreach (var (_, sub, slot) in mergedPatch.Where(x => x.Lod == lod))
            {
                var bl = slotBlocks[(lod, slot)];
                int b = 76 + sub * 88;
                if (done.Add(slot))
                    MeshDesc.PatchRecord(md, sub, bl.IOff, bl.ICount, bl.VOff, BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(b + 24)), BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(b + 28)),
                                         BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(b + 32)), BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(b + 36)));
                else MeshDesc.PatchRecord(md, sub, 0, 0, 0, 0, 0, 0, 0);
            }
            target.Touch(target.MeshId($"?lod={lod}&mesh"));
        }
    }

    // ------------------------------------------------------------------ textures

    /// <summary>Finds resources by id in the chassis ERP, the car's model, the template's other ERPs and the game.</summary>
    sealed class Finder(ErpFile entry, CarModel model, List<ErpFile> others, GameData game, int season)
    {
        Dictionary<string, ErpResource>? _entry, _others, _common;
        public ErpResource? Find(string id)
        {
            _entry ??= ErpX.Index(entry);
            if (_entry.TryGetValue(id, out var r)) return r;
            if (model.Res.TryGetValue(id, out r)) return r;
            if (_others is null) { _others = new(StringComparer.Ordinal); foreach (var e in others) foreach (var x in e.Resources) _others.TryAdd(x.Identifier, x); }
            if (_others.TryGetValue(id, out r)) return r;
            if (_common is null)
            {
                _common = new(StringComparer.Ordinal);
                var m = System.Text.RegularExpressions.Regex.Match(id, @"/teams/([a-z0-9_]+)/");
                var team = m.Success ? game.TeamErp(m.Groups[1].Value, season) : null;
                if (team is not null && File.Exists(team)) foreach (var x in ErpX.Load(team).Resources) _common.TryAdd(x.Identifier, x);
            }
            return _common.TryGetValue(id, out r) ? r : null;
        }
    }

    /// <summary>The rim textures (d, n, s) of the material a car's bindings put on its 'wheel' slot.</summary>
    static Dictionary<string, string> RimTextures(IEnumerable<string> bindings, Finder finder)
    {
        var tex = new Dictionary<string, string>();
        foreach (var emb in bindings)
        {
            var r = finder.Find(emb);
            if (r is null) continue;
            if (!EgoMaterial.ParseBinding(ErpX.Data(r)).TryGetValue("wheel", out var mat)) continue;
            var mr = finder.Find(mat);
            if (mr is null) continue;
            foreach (var t in ErpX.Eaids(ErpX.Data(mr)).Distinct())
            {
                var mm = System.Text.RegularExpressions.Regex.Match(t, @"_wheel_(d|n|s)\.tif$");
                if (mm.Success) tex.TryAdd(mm.Groups[1].Value, t);
            }
        }
        return tex;
    }

    /// <summary>A texture (SRV + surface) inside the ERP, copied in when it comes from elsewhere.</summary>
    static ErpResource InEntry(ErpFile entry, string id, Finder finder)
    {
        var have = entry.Resources.FirstOrDefault(r => r.Identifier == id);
        if (have is not null) return have;
        var srvRes = finder.Find(id) ?? throw new InvalidDataException($"{id} not found");
        var probe = new ErpGfxSRVResource();
        probe.FromResource(srvRes);
        var surface = finder.Find(probe.Fragment0.SurfaceResourceName) ?? throw new InvalidDataException($"the surface of {id} not found");
        // the finder's resources outside the ERP are fresh reads, so they can be adopted as they are
        ErpX.AddOrReplace(entry, surface);
        ErpX.AddOrReplace(entry, srvRes);
        return srvRes;
    }

    // ------------------------------------------------------------------ helpers (as the steering wheel graft's)

    /// <summary>A same-length private name for a model namespace: its last characters replaced by letters past hex ('g'
    /// to 'z'), so the steering wheel engine never reads it as its own earlier build.</summary>
    static string PrivateName(string ns, string donor)
    {
        uint h = 2166136261;
        foreach (var ch in ns + "|rims|" + donor) h = (h ^ ch) * 16777619;
        int k = Math.Min(4, ns.Length);
        var tail = new char[k];
        tail[0] = 'r';
        for (int i = 1; i < k; i++) { tail[i] = (char)('g' + h % 20); h /= 20; }
        var p = ns[..^k] + new string(tail);
        return p == ns ? ns[..^1] + (ns[^1] == 'z' ? 'y' : 'z') : p;
    }

    static int[]? StreamPlan(CarModel donor, CarModel target, MeshDesc m, SubMesh s, string[] tstreams)
    {
        var ss = m.StreamEaids(s).Select(e => donor.Buf(e)).ToArray();
        var used = new bool[ss.Length];
        var plan = new int[tstreams.Length];
        for (int k = 0; k < tstreams.Length; k++)
        {
            var b = target.Buf(tstreams[k]);
            int j = Array.FindIndex(ss, x => x.Stride == b.Stride && x.Fvf == b.Fvf);
            while (j >= 0 && used[j]) j = Array.FindIndex(ss, j + 1, x => x.Stride == b.Stride && x.Fvf == b.Fvf);
            if (j >= 0) { used[j] = true; plan[k] = j; }
            else if (b.Fvf == BufInfo.FvfHalf2 && b.Stride == 4) plan[k] = -1;
            else return null;
        }
        for (int j = 0; j < ss.Length; j++)
            if (!used[j] && (ss[j].Fvf == BufInfo.FvfPosition || ss[j].Fvf == BufInfo.FvfBones)) return null;
        return plan;
    }

    static uint[] ReadIndices(CarModel car, string eaid)
    {
        var data = car.Frags(eaid)[1];
        return car.Buf(eaid).Stride switch
        {
            2 => MemoryMarshal.Cast<byte, ushort>(data).ToArray().Select(x => (uint)x).ToArray(),
            4 => MemoryMarshal.Cast<byte, uint>(data).ToArray(),
            var st => throw new InvalidDataException($"unsupported index size {st}"),
        };
    }

    static byte[] WriteIndices(uint[] idx, int stride)
    {
        if (stride == 4) return MemoryMarshal.AsBytes(idx.AsSpan()).ToArray();
        var s = new ushort[idx.Length];
        for (int i = 0; i < idx.Length; i++) s[i] = checked((ushort)idx[i]);
        return MemoryMarshal.AsBytes(s.AsSpan()).ToArray();
    }

    static void SetCount(CarModel car, string eaid, int count)
    {
        var h = (byte[])car.Frags(eaid)[0].Clone();
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), count);
        car.Set(eaid, 0, h);
    }

    static Vector4 Sphere(List<float> p)
    {
        if (p.Count == 0) return default;
        float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue], mx = [float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < p.Count; i += 3)
            for (int k = 0; k < 3; k++) { mn[k] = Math.Min(mn[k], p[i + k]); mx[k] = Math.Max(mx[k], p[i + k]); }
        float cx = (mn[0] + mx[0]) / 2, cy = (mn[1] + mx[1]) / 2, cz = (mn[2] + mx[2]) / 2, r = 0;
        for (int i = 0; i < p.Count; i += 3)
        {
            float dx = p[i] - cx, dy = p[i + 1] - cy, dz = p[i + 2] - cz;
            r = Math.Max(r, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        return new Vector4(cx, cy, cz, r);
    }
}
