using System.Security.Cryptography;
using System.Text;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;

namespace HelmetSwap.Core;

/// <summary>Which parts of the donor's helmet a swap takes. With another make the design brings its shell, and the wing
/// and attachments are shaped for the donor's shell (they may not sit perfectly: the app warns, the preview shows it).</summary>
[Flags]
public enum SwapParts
{
    /// <summary>The paint: helmet material and textures (and, for another make, the donor's shell).</summary>
    Design = 1,
    /// <summary>The visor: its tint and the tear-offs.</summary>
    Visor = 2,
    /// <summary>The wing (rear spoiler): the "wing" surface and the clear triangles in the wing zone.</summary>
    Wing = 4,
    /// <summary>The attachments: clear vents and chin strip, and front vents painted on the wing texture (Schuberth).</summary>
    Attachments = 8,
    /// <summary>The visor strip: the sponsor strip above the visor (its own surface and material).</summary>
    VisorStrip = 16,
    All = Design | Visor | Wing | Attachments | VisorStrip,
}

/// <summary>
/// Whose each helmet part is: a donor's, or (null) the target's own. One donor for every picked part is the classic
/// swap; parts can also come from different helmets (Hülkenberg's wing, Hadjar's visor, ...).
/// </summary>
public sealed class PartPicks
{
    /// <summary>The parts, in the order they're shown and named.</summary>
    public static readonly SwapParts[] Each = [SwapParts.Design, SwapParts.Visor, SwapParts.VisorStrip, SwapParts.Wing, SwapParts.Attachments];
    readonly Dictionary<SwapParts, Driver> _from = [];

    public Driver? this[SwapParts part]
    {
        get => _from.GetValueOrDefault(part);
        set { if (value is null) _from.Remove(part); else _from[part] = value; }
    }

    /// <summary>The classic swap: <paramref name="parts"/> from <paramref name="donor"/>, the rest the target's own.</summary>
    public static PartPicks Of(Driver donor, SwapParts parts)
    {
        var p = new PartPicks();
        foreach (var part in Each) if (parts.HasFlag(part)) p[part] = donor;
        return p;
    }

    public PartPicks Clone() { var c = new PartPicks(); foreach (var (k, v) in _from) c._from[k] = v; return c; }

    /// <summary>The parts taken from a donor (any).</summary>
    public SwapParts Parts => _from.Keys.Aggregate((SwapParts)0, (a, p) => a | p);
    /// <summary>The parts taken from <paramref name="donor"/>.</summary>
    public SwapParts PartsFrom(Driver donor) => _from.Where(kv => kv.Value == donor).Aggregate((SwapParts)0, (a, kv) => a | kv.Key);
    /// <summary>The donors, each once, in part order.</summary>
    public List<Driver> Donors => Each.Where(_from.ContainsKey).Select(p => _from[p]).Distinct().ToList();
    /// <summary>The one donor of a classic swap, or null (none, or several).</summary>
    public Driver? Single => Donors is [var d] ? d : null;
    /// <summary>The first donor in part order (names private copies that aren't one donor's).</summary>
    public Driver? Primary => Donors.FirstOrDefault();
    /// <summary>Whose the part is in the finished helmet.</summary>
    public Driver From(SwapParts part, Driver target) => this[part] ?? target;
    public bool IsEmpty => _from.Count == 0;
}

/// <summary>What a swap produced.</summary>
public sealed class SwapResult
{
    public required Driver Target { get; init; }
    public required Driver Donor { get; init; }
    public required byte[] Erp { get; init; }
    /// <summary>Per helmet part: the material the target now uses (its own id, or a private id bound via its material bindings).</summary>
    public Dictionary<string, string> Materials { get; } = new();
    /// <summary>generic material id -> private id written into the target's material bindings.</summary>
    public Dictionary<string, string> BindingPatches { get; } = new();
    /// <summary>donor texture id -> private texture id that now carries its pixels.</summary>
    public Dictionary<string, string> Textures { get; } = new();
    /// <summary>donor attachments-model buffer id -> private buffer id (the target's mesh now reads these).</summary>
    public Dictionary<string, string> Buffers { get; } = new();
}

/// <summary>
/// Same-shell whole-helmet swap: the donor driver's helmet design (helmet, wing, visor and visor attachments: materials
/// and textures) onto the target driver, as a helmet template for the target.
/// <para>Rules: nothing shared changes and nothing of the donor is carried under the donor's own names (so the donor's
/// own template stays independent). Materials are binary with offset tables, so every renamed reference keeps its exact
/// length: new textures/materials get private names of the same length as the names they replace.</para>
/// </summary>
public static class HelmetSwapper
{
    /// <summary>Helmet material slots, by material file name (the "wing" slot uses helmet_wing.material). The shell
    /// uses helmet, visor and visor_tear; each driver's attachments model (wing, visor attachments, clear parts) the rest.</summary>
    public static readonly string[] Parts = ["helmet", "helmet_transparent", "helmet_attachments", "helmet_wing", "visor", "visor_attachments", "visor_tear"];
    public const string GenericRoot = "eaid://character_package/drivers/gender/teamname_charname/";

    /// <summary>Which part a material slot belongs to.</summary>
    public static SwapParts PartOf(string material) => material switch
    {
        "helmet" => SwapParts.Design,
        "visor" or "visor_tear" => SwapParts.Visor,
        "helmet_wing" => SwapParts.Wing,
        "visor_attachments" => SwapParts.VisorStrip,
        _ => SwapParts.Attachments,                       // helmet_transparent, helmet_attachments
    };

    /// <summary>The parts made from a driver's helmet parts model (shaped for that driver's shell).</summary>
    public const SwapParts ModelParts = SwapParts.Wing | SwapParts.Attachments | SwapParts.VisorStrip;

    /// <summary>Whose parts model each group comes from: its donor's when picked, else the target's own.</summary>
    public static (Driver Wing, Driver Strip, Driver Attachments) PartsFrom(Driver target, PartPicks picks) =>
        (picks.From(SwapParts.Wing, target), picks.From(SwapParts.VisorStrip, target), picks.From(SwapParts.Attachments, target));
    public static (Driver Wing, Driver Strip, Driver Attachments) PartsFrom(Driver target, Driver donor, SwapParts parts) => PartsFrom(target, PartPicks.Of(donor, parts));

    /// <summary>When the wing and the attachments come from different drivers and the attachments driver has front vents
    /// on its wing surface, those vents move to the spare <see cref="HelmetParts.VentsSlot"/> surface, which is bound to a
    /// copy of that driver's wing material; null otherwise.</summary>
    public static Driver? VentsFrom(Driver target, PartPicks picks)
    {
        var (wing, _, att) = PartsFrom(target, picks);
        return wing != att && HasFrontVents(att) ? att : null;
    }
    public static Driver? VentsFrom(Driver target, Driver donor, SwapParts parts) => VentsFrom(target, PartPicks.Of(donor, parts));

    static readonly Dictionary<string, bool> _vents = [];
    /// <summary>Does the driver's parts model have "wing" triangles outside the wing zone (front vents)?</summary>
    public static bool HasFrontVents(Driver d)
    {
        lock (_vents)
        {
            if (_vents.TryGetValue(d.HelmetErp, out var known)) return known;
            var idx = ErpX.Index(ErpX.Load(d.HelmetErp));
            bool any = false;
            if (idx.TryGetValue(Attachments(d) + "lod=0&mesh", out var mr))
            {
                var m = MeshDesc.Parse(ErpX.Data(mr));
                var ib = ErpX.Data(idx[m.IndexBuffer], 1);
                for (int s = 0; s < m.Records.Count && !any; s++)
                {
                    if (m.Slots[s] != "wing") continue;
                    var pos = ErpX.Data(idx[m.Streams[m.StreamsOf(s)[0]]], 1);
                    var (off, cnt) = m.IndexRange(s); int bv = BitConverter.ToInt32(m.Records[s], 20);
                    for (int t = 0; t + 2 < cnt && !any; t += 3)
                    {
                        var v = Enumerable.Range(0, 3).Select(k => MeshDesc.Position(pos, BitConverter.ToUInt16(ib, (off + t + k) * 2) + bv)).ToArray();
                        any = !HelmetParts.InWingZone(v[0], v[1], v[2]);
                    }
                }
            }
            return _vents[d.HelmetErp] = any;
        }
    }

    /// <summary>"Design", "Visor + Wing", ...; the whole helmet is named by its make.</summary>
    public static string PartsLabel(SwapParts parts) =>
        string.Join(" + ", new[] { (SwapParts.Design, "Shell"), (SwapParts.Visor, "Visor"), (SwapParts.VisorStrip, "Visor strip"), (SwapParts.Wing, "Wing"), (SwapParts.Attachments, "Attachments") }
                               .Where(p => parts.HasFlag(p.Item1)).Select(p => p.Item2));

    /// <summary>Why these picks can't be built, or null when they can: each donor's parts by the rules below.</summary>
    public static string? PartsProblem(Driver target, PartPicks picks)
    {
        if (picks.IsEmpty) return "Pick at least one part to swap.";
        foreach (var d in picks.Donors)
            if (PartsProblem(target, d, picks.PartsFrom(d)) is { } problem) return problem;
        return null;
    }

    /// <summary>Why these parts can't be swapped, or null when they can.</summary>
    public static string? PartsProblem(Driver target, Driver donor, SwapParts parts)
    {
        if ((parts & SwapParts.All) == 0) return "Pick at least one part to swap.";
        if (donor.IsCareerDesign) return "A standard career design's colours are picked in game, so it can't be a donor (pick a driver or a replica helmet).";
        if (target.IsCareerDesign)
        {
            // most standard designs share one helmet parts model (My Team's): only materials can change, per design
            if ((parts & ModelParts) != 0 && target.AttachmentsRoot is not null)
                return "This career design shares its helmet parts with the other designs, so only the shell and the visor can change (visor strip, wing and attachments stay its own).";
            if (parts.HasFlag(SwapParts.Design) && donor.Shell != target.Shell)
                return $"Standard career designs wear an {target.ShellName}: the shell must come from an {target.MakeName} helmet (the visor can come from any).";
        }
        if (ShellSwap(target, donor, parts) && !target.OwnShellBinding)
            return $"{target.Name}'s helmet files have no shell binding of their own (it is shared with the whole team), so their {target.ShellName} " +
                   $"can't be hidden without changing teammates: another make's shell can't go on (the visor, visor strip, wing or attachments can).";
        return null;
    }

    /// <summary>The donor's shell comes along: a design of another make is painted for its own shell.</summary>
    public static bool ShellSwap(Driver target, Driver donor, SwapParts parts) => target.Shell != donor.Shell && parts.HasFlag(SwapParts.Design);
    public static bool ShellSwap(Driver target, PartPicks picks) => picks[SwapParts.Design] is { } d && d.Shell != target.Shell;

    /// <summary>A warning for picks whose parts are shaped for another shell than the one the helmet ends up with, or for a
    /// visor strip from a helmet that has none; null when there's nothing to say. One donor: as <see cref="PartsWarning(Driver, Driver, SwapParts)"/>.</summary>
    public static string? PartsWarning(Driver target, PartPicks picks)
    {
        if (picks.Single is { } one) return PartsWarning(target, one, picks.PartsFrom(one));
        if (picks[SwapParts.VisorStrip] is { HasStrip: false } ns && target.HasStrip)
            return $"{ns.Name}'s {(ns.YearTag == "" ? "" : ns.YearTag + " ")}helmet has no visor strip, so {target.Name} will have none (pick another visor strip to keep one).";
        // the shell the helmet ends up with: the design's
        var shellFrom = picks.From(SwapParts.Design, target);
        var off = new[] { SwapParts.Wing, SwapParts.VisorStrip, SwapParts.Attachments }
            .Select(p => (Part: p, From: picks.From(p, target))).Where(x => x.From.Shell != shellFrom.Shell).ToList();
        if (off.Count == 0) return null;
        return $"Different make: " + string.Join("; ", off.GroupBy(x => x.From).Select(g =>
                   $"{g.Key.Name}'s {PartsLabel(g.Aggregate((SwapParts)0, (a, x) => a | x.Part)).ToLowerInvariant()} {(g.Count() == 1 && g.First().Part != SwapParts.Attachments ? "is" : "are")} shaped for the {g.Key.ShellName}")) +
               $", so on the {shellFrom.ShellName} {(off.Count == 1 && off[0].Part != SwapParts.Attachments ? "it" : "they")} may not sit perfectly. Check the preview.";
    }

    /// <summary>A warning for parts of another make that are shaped for a different shell, or null.</summary>
    public static string? PartsWarning(Driver target, Driver donor, SwapParts parts)
    {
        // a helmet without a visor strip (2024 F2 Isack Hadjar): taking "its" strip takes the strip away
        if (parts.HasFlag(SwapParts.VisorStrip) && !donor.HasStrip && target.HasStrip)
            return $"{donor.Name}'s {(donor.YearTag == "" ? "" : donor.YearTag + " ")}helmet has no visor strip, so {target.Name} will have none (untick visor strip to keep theirs).";
        if (target.Shell == donor.Shell || parts == SwapParts.All || (parts & (SwapParts.Design | ModelParts)) == 0) return null;
        // "the wing is ... it", "the attachments / wing + attachments are ... they"
        static (string Is, string It) Verb(SwapParts p) => p is SwapParts.Wing or SwapParts.VisorStrip ? ("is", "it") : ("are", "they");
        if (parts.HasFlag(SwapParts.Design))
        {
            var own = SwapParts.All & ~parts & ModelParts;
            var (ownIs, ownIt) = Verb(own);
            return $"Different make: the {donor.ShellName} shell" + (own == 0 ? "." :
                   $", and {target.Name}'s own {PartsLabel(own).ToLowerInvariant()} {ownIs} shaped for the {target.ShellName}, so {ownIt} may not sit perfectly. Check the preview.");
        }
        var taken = parts & ModelParts;
        var (takenIs, takenIt) = Verb(taken);
        return $"Different make: {donor.Name}'s {PartsLabel(taken).ToLowerInvariant()} {takenIs} shaped for the {donor.ShellName}, " +
               $"so on {target.Name}'s {target.ShellName} {takenIt} may not sit perfectly. Check the preview.";
    }

    public static string MainErp(Driver d) => Path.Combine(Path.GetDirectoryName(d.HelmetErp)!, d.Token + ".erp");
    /// <summary>The ERPs that hold a driver's helmet resources and material bindings (career helmets have no body ERP).</summary>
    static IEnumerable<string> HelmetErps(Driver d) => File.Exists(MainErp(d)) ? [d.HelmetErp, MainErp(d)] : [d.HelmetErp];
    /// <summary>The other ERPs a driver's helmet reads from, for drawing it: the body ERP, and for career helmets the
    /// career common helmet ERPs (the standard designs' Arai shell is there, not in each design's ERP).</summary>
    public static IEnumerable<string> ReadErps(GameInstall game, Driver d) =>
        new[] { MainErp(d) }.Concat(d.IsCareer ? new[] { "tier_common_helmet.erp", "novelty_common_helmet.erp" }.Select(f => Path.Combine(game.Assets, "livery_package", f)) : Enumerable.Empty<string>())
            .Where(File.Exists);
    public static string CommonHelmetErp(GameInstall game) => Path.Combine(game.DriversDir, $"common_{"male"}_helmet.erp");

    /// <summary>"Charles Leclerc - Schuberth Swap (Max V)": who changes, the make they end up wearing, whose helmet
    /// (older / special helmets add which: "(Lewis H 2024)", "(Max V Japan 2025)"). Some parts only:
    /// "Charles Leclerc - Wing Swap (Lando N)", "... - Shell + Visor Swap (...)".</summary>
    public static string SwapName(Driver target, Driver donor, SwapParts parts = SwapParts.All) =>
        $"{target.Label}{(target.YearTag == "" ? "" : " " + target.YearTag)} - {(parts == SwapParts.All ? donor.MakeName : PartsLabel(parts))} Swap " +
        $"({donor.ShortName}{(donor.YearTag == "" ? "" : " " + donor.YearTag)})";
    public static string OutputName(Driver target, Driver donor, SwapParts parts = SwapParts.All) => $"{SwapName(target, donor, parts)}.serps";
    public static string TemplateName(Driver target, Driver donor, SwapParts parts = SwapParts.All) => $"{SwapName(target, donor, parts)} - SERPs Template";

    /// <summary>One donor: as above. Parts from several helmets: "Charles Leclerc - Mixed Swap (Nico H, Isack H 2024)"
    /// (the parts folders inside say which part is whose).</summary>
    public static string SwapName(Driver target, PartPicks picks) => picks.Single is { } one ? SwapName(target, one, picks.PartsFrom(one))
        : $"{target.Label}{(target.YearTag == "" ? "" : " " + target.YearTag)} - Mixed Swap ({string.Join(", ", picks.Donors.Select(d => d.ShortName + (d.YearTag == "" ? "" : " " + d.YearTag)))})";
    public static string OutputName(Driver target, PartPicks picks) => $"{SwapName(target, picks)}.serps";
    public static string TemplateName(Driver target, PartPicks picks) => $"{SwapName(target, picks)} - SERPs Template";

    /// <summary>The driver's main material binding (lists the material of every slot).</summary>
    public static string MainBinding(Driver d) => $"{d.EaidRoot}idf/{d.Token}.emb?context={d.Folder}";

    /// <summary>The material a driver's binding uses for a helmet part: their own, or the shared generic one.</summary>
    public static string BoundMaterial(IReadOnlyDictionary<string, ErpResource> idx, Driver d, string part)
    {
        // the driver's own binding; a few drivers (an unfinished duplicate, e.g. F2 Alex Dunne) only have ?context=default
        if (!idx.TryGetValue(MainBinding(d), out var emb) && !idx.TryGetValue($"{d.EaidRoot}idf/{d.Token}.emb?context=default", out emb))
            throw new InvalidDataException($"{d.Name}: material binding {MainBinding(d).Split('/')[^1]} not found");
        var hits = ErpX.Eaids(ErpX.Data(emb)).Where(e => e.EndsWith($"/{part}.material", StringComparison.Ordinal) || IsPrivateFor(e, d) && e.EndsWith($"_{part}.material", StringComparison.Ordinal))
                       .Distinct().ToList();
        if (hits.Count != 1) throw new InvalidDataException($"{d.Name}: {(hits.Count == 0 ? "no" : "several")} '{part}' materials in the material binding");
        return hits[0];
    }

    internal static bool IsPrivateFor(string id, Driver d) => id.StartsWith(d.EaidRoot + "hsw/", StringComparison.Ordinal) || id.StartsWith(d.EaidRoot + "idf/hsw", StringComparison.Ordinal)
                                                     || id.StartsWith("eaid://character_package/hsw/", StringComparison.Ordinal);

    /// <summary>The per-driver helmet attachments model (wing, visor attachments, clear parts), e.g. ".../idf/lando_norris_helmet_attachments.idf?".</summary>
    public static string Attachments(Driver d) => d.AttachmentsRoot ?? $"{d.EaidRoot}idf/{d.Token}_helmet_attachments.idf?";

    /// <summary>Bytes of the ?model header that hold the model's bounds (bounding sphere, box, draw distance), which
    /// follow its mesh; the rest of the model (skeleton, node table) is the same for every driver.</summary>
    public const int BoundsStart = 16, BoundsEnd = 68;

    public static SwapResult Build(GameInstall game, Driver target, Driver donor, SwapLog log, SwapParts parts = SwapParts.All) =>
        Build(game, target, PartPicks.Of(donor, parts), log);

    /// <summary>The swap: each picked part from its donor (one donor = the classic swap, parts from several helmets = a
    /// mixed one), the rest the target's own; one ERP of everything the target's helmet needs changed.</summary>
    public static SwapResult Build(GameInstall game, Driver target, PartPicks picks, SwapLog log)
    {
        var donors = picks.Donors;
        foreach (var d in donors) if (target.Folder == d.Folder) throw new InvalidOperationException($"{target.Name} already has that helmet.");
        if (PartsProblem(target, picks) is { } problem) throw new InvalidOperationException(problem);
        if (target.Shell is null || donors.Any(d => d.Shell is null)) throw new InvalidOperationException("helmet shell not recognised");
        var primary = picks.Primary!;
        var shellFrom = picks[SwapParts.Design] ?? primary;          // whose shell comes along (with another make's design)
        bool otherShell = ShellSwap(target, picks);
        if (otherShell && !target.OwnShellBinding)
            throw new InvalidOperationException($"{target.Name}'s helmet files have no shell binding of their own (it is shared with the whole team), so their " +
                                                $"{target.ShellName} can't be hidden without changing teammates: pick a {target.ShellName} donor.");

        var outErp = ErpX.Load(target.HelmetErp);            // output container (header kept, resources replaced below)
        var tIdx = ErpX.Index(outErp);
        if (File.Exists(MainErp(target)))
            foreach (var r in ErpX.Load(MainErp(target)).Resources) tIdx.TryAdd(r.Identifier, r);
        var dIdxs = new Dictionary<Driver, Dictionary<string, ErpResource>>();
        Dictionary<string, ErpResource> DIdx(Driver d) => dIdxs.TryGetValue(d, out var i) ? i : dIdxs[d] = ErpX.Index(ErpX.Load(d.HelmetErp));
        var common = ErpX.Index(ErpX.Load(CommonHelmetErp(game)));
        var keep = new List<ErpResource>();
        var result = new SwapResult { Target = target, Donor = primary, Erp = [] };

        foreach (var (part, src, srcPart) in MaterialJobs(target, picks))
        {
            var sIdx = src == target ? tIdx : DIdx(src);
            var dm = BoundMaterial(sIdx, src, srcPart);
            var tm = BoundMaterial(tIdx, target, part);
            bool dGeneric = dm.StartsWith(GenericRoot, StringComparison.Ordinal), tGeneric = tm.StartsWith(GenericRoot, StringComparison.Ordinal);
            if (dGeneric && dm == tm) { log.Info($"{part}: both use the game's generic {part} material; nothing to change."); result.Materials[part] = tm; continue; }

            // the source material, from a fresh load (it becomes the target's resource)
            ErpResource source;
            if (dGeneric) source = ErpX.Index(ErpX.Load(CommonHelmetErp(game)))[dm];
            else
            {
                var fresh = ErpX.Index(ErpX.Load(src.HelmetErp));
                if (src == target && File.Exists(MainErp(target))) foreach (var r in ErpX.Load(MainErp(target)).Resources) fresh.TryAdd(r.Identifier, r);
                source = fresh[dm];
            }
            var content = ErpX.Data(source);
            // the donor's own textures get private copies; the target's own stay where the game has them
            foreach (var tex in src == target ? new List<string>() : ErpX.Eaids(content).Distinct().Where(src.OwnsTexture).ToList())
            {
                // the donor's own texture: embed its full-resolution pixels under a private same-length name
                var priv = PrivateId(tex, target, src);
                if (!result.Textures.ContainsKey(tex))
                {
                    keep.AddRange(PrivateTexture(game, src, tex, priv));
                    result.Textures[tex] = priv;
                }
                ErpX.ReplaceEaid(content, tex, priv);
            }
            string id;
            if (!tGeneric) id = tm;                                       // the target's own material: overwrite it
            else
            {
                id = PrivateId(tm, target, src == target ? primary : src);  // the target uses the shared one: bind a private copy
                result.BindingPatches[tm] = id;
            }
            source.Fragments[0].SetData(content, true);
            source.Identifier = id;
            ErpX.Rehash(source);
            keep.Add(source);
            result.Materials[part] = id;
            log.Info($"{part}: {(dGeneric ? "the game's generic" : src.Name + "'s")} {srcPart} material -> {(tGeneric ? "new private material bound for " + target.Name : target.Name + "'s " + part + " material")}"
                     + (part != srcPart ? " (the front vents' own surface)" : ""));
        }

        var parts = picks.Parts;
        if ((parts & ModelParts) != 0 || otherShell)
        {
            var (wingFrom, stripFrom, attFrom) = PartsFrom(target, picks);
            GraftModel(target, wingFrom, stripFrom, attFrom, shellFrom, otherShell, VentsFrom(target, picks) is not null, keep, result, log);
        }
        if (PartsWarning(target, picks) is { } warn) log.Warn(warn);
        if (picks.Single is null)
            log.Info($"Swapped: {string.Join(", ", donors.Select(d => $"{PartsLabel(picks.PartsFrom(d)).ToLowerInvariant()} from {d.Name}"))}" +
                     (parts == SwapParts.All ? "." : $"; {target.Name} keeps their own {PartsLabel(SwapParts.All & ~parts).ToLowerInvariant()}."));
        else if (parts != SwapParts.All) log.Info($"Swapped: {PartsLabel(parts).ToLowerInvariant()}; {target.Name} keeps their own {PartsLabel(SwapParts.All & ~parts).ToLowerInvariant()}.");

        // bindings: the target's own material bindings (both of its ERPs) that use a shared material now replaced
        if (result.BindingPatches.Count > 0)
        {
            foreach (var path in HelmetErps(target))
                foreach (var emb in ErpX.Load(path).Resources.Where(r => r.ResourceType == "GfxMatBindRes" && r.Identifier.Contains(target.Folder, StringComparison.Ordinal)))
                {
                    if (keep.Any(k => k.Identifier == emb.Identifier)) continue;
                    var d = ErpX.Data(emb);
                    int n = result.BindingPatches.Sum(p => ErpX.ReplaceEaid(d, p.Key, p.Value));
                    if (n == 0) continue;
                    emb.Fragments[0].SetData(d, true); ErpX.Rehash(emb);
                    keep.Add(emb);
                    log.Info($"Material binding {emb.Identifier.Split('/')[^1]}: {n} reference(s) to shared materials now point at {target.Name}'s private copies.");
                }
        }

        if (otherShell) HideShell(game, target, shellFrom, keep, log);

        outErp.Resources.Clear();
        foreach (var r in keep) ErpX.AddOrReplace(outErp, r);
        log.Info($"{keep.Count} resources: {keep.Count(r => r.ResourceType == "GfxMaterialRes")} materials, {result.Textures.Count} textures, " +
                 (result.Buffers.Count > 0 ? $"helmet parts model ({result.Buffers.Count} buffers), " : "") +
                 $"{keep.Count(r => r.ResourceType == "GfxMatBindRes")} material bindings.");
        return new SwapResult { Target = target, Donor = primary, Erp = ErpX.Save(outErp) }.With(result);
    }

    /// <summary>
    /// The new helmet 3D parts, through the target's own per-driver attachments model (the only helmet model that is the
    /// target's alone; the shell is shared by every driver of that make): the wing from <paramref name="wingFrom"/>, the
    /// clear trim and visor tab from <paramref name="trimFrom"/> (each the donor or the target), and with
    /// <paramref name="withShell"/> the donor's shell (LOD 0: helmet, visor, tear-off) merged in, the target's own shell
    /// being hidden for the target only (<see cref="HideShell"/>). Wing and trim from the same driver: that driver's
    /// whole parts mesh, as it is; from two drivers: each mesh filtered triangle by triangle (<see cref="HelmetParts"/>)
    /// and the two merged, with one index buffer.
    /// Vertex/index buffers are carried under private names. The target's model keeps its skeleton; node groups are
    /// re-pointed by node name (node tables differ in order between drivers; the bone palette, which the vertices
    /// index, is the same everywhere and is checked). Bounds cover the new parts.
    /// </summary>
    static void GraftModel(Driver target, Driver wingFrom, Driver stripFrom, Driver attFrom, Driver donor, bool withShell, bool moveVents,
        List<ErpResource> keep, SwapResult result, SwapLog log)
    {
        var tIdx = ErpX.Index(ErpX.Load(target.HelmetErp));
        string tA = Attachments(target);
        ErpResource Need(Dictionary<string, ErpResource> idx, Driver d, string id) =>
            idx.TryGetValue(id, out var r) ? r : throw new InvalidDataException($"{d.Name}: helmet model piece {id.Split('/')[^1]} not found");
        var tModel = Need(tIdx, target, tA + "model");
        var tRender = ErpX.Data(Need(tIdx, target, tA + "render"));
        Need(tIdx, target, tA + "lod=0&mesh");
        if (!ErpX.Eaids(ErpX.Data(tModel)).SequenceEqual([tA + "lod=0&mesh"])) throw new InvalidDataException("helmet parts model: unexpected layout (more than one mesh)");
        var palette = Palette(tRender);
        var tNodes = NodeNames(tRender);

        // a private copy of a buffer the new mesh reads
        string CarryRes(ErpResource res, Driver from, string buf)
        {
            var priv = PrivateId(buf, target, from, model: true);
            res.Identifier = priv;
            ErpX.Rehash(res);
            keep.Add(res);
            result.Buffers[buf] = priv;
            return priv;
        }
        string Carry(Dictionary<string, ErpResource> idx, Driver from, string buf) => CarryRes(Need(idx, from, buf), from, buf);

        // one driver's parts mesh (optionally filtered), nodes re-pointed to the target's model, streams carried
        (MeshDesc Mesh, byte[] Ib, ErpResource IbRes, string IbName, Driver From, byte[] Model) Source(Driver from, SwapParts? take)
        {
            var idx = ErpX.Index(ErpX.Load(from.HelmetErp));
            string a = Attachments(from);
            var model = ErpX.Data(Need(idx, from, a + "model"));
            var render = ErpX.Data(Need(idx, from, a + "render"));
            if (!ErpX.Eaids(model).SequenceEqual([a + "lod=0&mesh"])) throw new InvalidDataException("helmet parts model: unexpected layout (more than one mesh)");
            // the source's vertices index its palette: the target's must start with the same bones (a donor whose
            // parts have nothing on the visor can stop at bone_helmet_main, e.g. 2024 F2 Isack Hadjar)
            if (!palette.StartsWith(Palette(render), StringComparison.Ordinal)) throw new InvalidDataException($"{target.Name} and {from.Name} have different helmet skeletons");
            var nodes = NodeNames(render);
            uint Node(uint n) => (uint)(tNodes.IndexOf(nodes[(int)n]) is var i and >= 0 ? i
                : throw new InvalidDataException($"{target.Name}'s helmet model has no node '{nodes[(int)n]}'"));
            var mesh = MeshDesc.Parse(ErpX.Data(Need(idx, from, a + "lod=0&mesh")));
            var ibRes = Need(idx, from, mesh.IndexBuffer);
            var ibName = mesh.IndexBuffer;
            var ib = ErpX.Data(ibRes, 1);
            if (take is { } groups)
            {
                var orig = mesh; bool hasWing = from.Spoiler != "no wing";
                var pos = new Dictionary<int, byte[]>();
                byte[] Pos(int sub) => pos.TryGetValue(sub, out var p) ? p : pos[sub] = ErpX.Data(Need(idx, from, orig.Streams[orig.StreamsOf(sub)[0]]), 1);
                mesh = orig.Filter(ib, (sub, i0, i1, i2) => groups.HasFlag(HelmetParts.ClassOf(orig.Slots[sub], hasWing,
                    MeshDesc.Position(Pos(sub), i0), MeshDesc.Position(Pos(sub), i1), MeshDesc.Position(Pos(sub), i2))), out ib);
                // the attachments driver's front vents (its wing surface outside the wing zone) on their own surface
                if (moveVents && from == attFrom) mesh.Slots = [.. mesh.Slots.Select(s => s == "wing" ? HelmetParts.VentsSlot : s)];
            }
            foreach (var g in mesh.Groups) BitConverter.TryWriteBytes(g.AsSpan(68), Node(BitConverter.ToUInt32(g, 68)));
            mesh.Nodes = [.. mesh.Nodes.Select(n => (Node(n.Node), n.Sub))];
            mesh.Streams = [.. mesh.Streams.Select(x => Carry(idx, from, x))];
            return (mesh, ib, ibRes, ibName, from, model);
        }

        MeshDesc mesh; byte[] ib; ErpResource ibRes; string ibName; Driver ibFrom; float[] bounds; string slots;
        bool ibChanged = false;
        var groupsOf = GroupsOf(wingFrom, stripFrom, attFrom);
        if (groupsOf.Count == 1)
        {
            var one = Source(wingFrom, null);
            (mesh, ib, ibRes, ibName, ibFrom) = (one.Mesh, one.Ib, one.IbRes, one.IbName, one.From);
            bounds = Bounds(one.Model);
            slots = $"{wingFrom.Name}'s {string.Join(", ", mesh.Slots)}";
        }
        else
        {
            // each driver's groups, filtered triangle by triangle, merged into one mesh with one index buffer
            var parts = groupsOf.Select(g => (Src: Source(g.From, g.Groups), g.Groups)).ToList();
            var first = parts[0].Src;
            (mesh, ib, ibRes, ibName, ibFrom) = (first.Mesh, first.Ib, first.IbRes, first.IbName, first.From);
            bounds = Bounds(first.Model);
            foreach (var (src, _) in parts.Skip(1))
            {
                mesh = MeshDesc.Merge(mesh, src.Mesh, ib.Length / 2, null);
                ib = [.. ib, .. src.Ib];
                bounds = Union(bounds, Bounds(src.Model));
            }
            ibChanged = true;
            slots = string.Join(" + ", parts.Select(p => $"{p.Src.From.Name}'s {PartsLabel(p.Groups).ToLowerInvariant()} ({p.Src.Ib.Length / 6} triangles)"))
                    + (moveVents ? $" ({attFrom.Name}'s front vents on their own surface)" : "");
        }
        int ibCount = BitConverter.ToInt32(ErpX.Data(ibRes, 0), 8);
        if (ibChanged) ibCount = ib.Length / 2;
        if (withShell)
        {
            string s = ShellRoot(donor.Shell!);
            var sIdx = ErpX.Index(ErpX.Load(donor.HelmetErp));          // the donor's copy of its shell
            var shell = MeshDesc.Parse(ErpX.Data(Need(sIdx, donor, s + "lod=0&mesh")));
            var sRender = ErpX.Data(Need(sIdx, donor, s + "render"));
            if (!Palette(sRender).StartsWith(palette, StringComparison.Ordinal))
                throw new InvalidDataException($"the {donor.ShellName} shell is skinned to different bones");
            shell.Streams = [.. shell.Streams.Select(x => Carry(sIdx, donor, x))];
            // one index buffer: the parts' indices, then the shell's
            var sIb = Need(sIdx, donor, shell.IndexBuffer);
            if (BitConverter.ToInt32(ErpX.Data(ibRes, 0), 4) != 2 || BitConverter.ToInt32(ErpX.Data(sIb, 0), 4) != 2) throw new InvalidDataException("unexpected index format");
            var helmetNode = (uint)tNodes.IndexOf("helmet_attachments_x0");
            if ((int)helmetNode < 0) throw new InvalidDataException($"{target.Name}'s helmet model has no helmet_attachments_x0 node");
            mesh = MeshDesc.Merge(mesh, shell, ibCount, helmetNode);
            ibCount += BitConverter.ToInt32(ErpX.Data(sIb, 0), 8);
            ib = [.. ib, .. ErpX.Data(sIb, 1)];
            ibChanged = true;
            result.Buffers[shell.IndexBuffer] = PrivateId(ibName, target, ibFrom, model: true);
            slots += $" + {donor.ShellName} shell ({string.Join(", ", shell.Slots)})";
        }
        if (ibChanged)
        {
            var head = ErpX.Data(ibRes, 0);
            if (BitConverter.ToInt32(head, 4) != 2) throw new InvalidDataException("unexpected index format");
            BitConverter.TryWriteBytes(head.AsSpan(8), ibCount);
            ibRes.Fragments[0].SetData(head, true);
            ibRes.Fragments[1].SetData(ib, true);
        }
        mesh.IndexBuffer = CarryRes(ibRes, ibFrom, ibName);

        var meshRes = Need(ErpX.Index(ErpX.Load(target.HelmetErp)), target, tA + "lod=0&mesh");
        meshRes.Fragments[0].SetData(mesh.Write(), true);
        ErpX.Rehash(meshRes);
        keep.Add(meshRes);

        var modelBytes = ErpX.Data(tModel);
        if (withShell)
        {
            var shellModel = ErpX.Data(Need(ErpX.Index(ErpX.Load(donor.HelmetErp)), donor, ShellRoot(donor.Shell!) + "model"));
            bounds = Union(bounds, Bounds(shellModel));
            // the helmet parts model has one detail level, drawn to 100; a shell's levels reach much further (524). The
            // donor shell now lives in the parts model and the target's own shell is hidden at every level, so the
            // parts model must be drawn as far as a shell is, or the driver shows no helmet from far away.
            bounds[12] = Math.Max(bounds[12], DrawDistance(shellModel));
        }
        WriteBounds(modelBytes, bounds);
        tModel.Fragments[0].SetData(modelBytes, true);
        ErpX.Rehash(tModel);
        keep.Add(tModel);
        log.Info($"helmet model: {slots} -> {target.Name}'s helmet");
    }

    /// <summary>The drivers the parts model is made from, with the groups each gives: the attachments driver first, then the
    /// wing's, then the visor strip's (one entry when all three are the same driver).</summary>
    static List<(Driver From, SwapParts Groups)> GroupsOf(Driver wingFrom, Driver stripFrom, Driver attFrom)
    {
        var list = new List<(Driver From, SwapParts Groups)>();
        void Add(Driver d, SwapParts g)
        {
            int i = list.FindIndex(x => x.From == d);
            if (i >= 0) list[i] = (d, list[i].Groups | g); else list.Add((d, g));
        }
        Add(attFrom, SwapParts.Attachments); Add(wingFrom, SwapParts.Wing); Add(stripFrom, SwapParts.VisorStrip);
        return list;
    }

    /// <summary>The materials a swap writes: (the target's part, from whom, which of their parts). Every picked part from
    /// the donor; and, when the front vents move (<see cref="VentsFrom"/>), their surface from that driver's wing material.</summary>
    static List<(string Part, Driver From, string FromPart)> MaterialJobs(Driver target, PartPicks picks)
    {
        var jobs = Parts.Where(p => picks[PartOf(p)] is not null).Select(p => (p, picks[PartOf(p)]!, p)).ToList();
        if (VentsFrom(target, picks) is { } v)
        {
            jobs.RemoveAll(j => j.Item1 == HelmetParts.VentsSlot);
            jobs.Add((HelmetParts.VentsSlot, v, "helmet_wing"));
        }
        return jobs;
    }

    public static string ShellRoot(string shell) => $"eaid://character_package/kitpart_assets/generic/helmets/{shell}/idf/{shell}.idf?";
    /// <summary>The target's own material binding for the shell it wears (shell slots helmet, visor, visor_tear).</summary>
    /// <summary>How the target's own shell is hidden: "tear" (default, confirmed in-game: the clear tear-off material
    /// at opacity 0; its refraction can show as static in close-ups) or "logo" (experiment: the game's generic helmet
    /// logo material, the MSDF decal shader with the empty blank_da logo, which draws nothing and does not refract).</summary>
    public static string HideWith { get; set; } = "tear";

    public static string ShellBinding(Driver d) => $"eaid://character_package/kitpart_assets/generic/helmets/{d.Shell}/idf/{d.Shell}.emb?context={d.Folder}";
    public static readonly string[] ShellParts = ["helmet", "visor", "visor_tear"];

    /// <summary>
    /// The target's own shell, made invisible for the target only: in the target's binding for that shell, the helmet,
    /// visor and tear-off slots point at private materials made from the game's tear-off material (the clear film
    /// over the visor; its shader already draws on every shell) with opacity 0 and refraction 1 (the background passes straight
    /// through). Roughness stays the game's 0: on this refractive shader, roughness blurs the background seen through
    /// it, which showed as static where the old shell pokes through the new one. The shell itself is shared by every driver of that make and is not touched.
    /// </summary>
    static void HideShell(GameInstall game, Driver target, Driver donor, List<ErpResource> keep, SwapLog log)
    {
        var id = ShellBinding(target);
        var emb = keep.FirstOrDefault(k => k.Identifier == id);
        if (emb is null)
        {
            emb = ErpX.Index(ErpX.Load(target.HelmetErp)).GetValueOrDefault(id) ?? throw new InvalidDataException($"{target.Name}: shell material binding not found");
            keep.Add(emb);
        }
        var data = ErpX.Data(emb);
        var refs = ErpX.Eaids(data).Distinct().ToList();
        int n = 0;
        foreach (var part in ShellParts)
        {
            var m = refs.Where(e => e.EndsWith($"/{part}.material", StringComparison.Ordinal) || IsPrivateFor(e, target) && e.EndsWith($"_{part}.material", StringComparison.Ordinal)).ToList();
            if (m.Count != 1) throw new InvalidDataException($"{target.Name}: the shell binding has {m.Count} '{part}' materials");
            var hidden = PrivateId(m[0], target, donor, salt: "hidden");
            var mat = ErpX.Index(ErpX.Load(CommonHelmetErp(game)))[GenericRoot + (HideWith == "logo" ? "materials/helmet_logo.material" : "materials/visor_tear.material")];
            var bytes = ErpX.Data(mat);
            if (HideWith != "logo")
            {
                MaterialParams.Set(bytes, "me_alpha", 0f);
                MaterialParams.Set(bytes, "me_indexOfRefraction", 1f);
                // me_roughness stays 0 (see above)
            }
            mat.Fragments[0].SetData(bytes, true);
            mat.Identifier = hidden;
            ErpX.Rehash(mat);
            keep.Add(mat);
            n += ErpX.ReplaceEaid(data, m[0], hidden);
        }
        emb.Fragments[0].SetData(data, true);
        ErpX.Rehash(emb);
        log.Info($"{target.Name}'s own {target.ShellName} shell hidden ({n} slots bound to an invisible material, for {target.Name} only).");
    }

    /// <summary>How far a model is drawn: u32[0] = detail levels (LODs); from byte 64 the levels' distances (level 0's
    /// end, then a start/end pair per further level), e.g. shells 4 | 4-10 | 10-24 | 24-58 | 58-524, helmet parts 100.</summary>
    public static float DrawDistance(byte[] model)
    {
        int lods = BitConverter.ToInt32(model, 0);
        return BitConverter.ToSingle(model, BoundsEnd - 4 + Math.Max(0, lods - 1) * 8);
    }

    /// <summary>Model bounds (bytes 16..68 of ?model): bounding sphere (centre, radius), 0, box min, box max, 0, draw distance
    /// (for a one-level model such as the helmet parts: the distance it is drawn to).</summary>
    static float[] Bounds(byte[] model) => Enumerable.Range(0, 13).Select(i => BitConverter.ToSingle(model, BoundsStart + i * 4)).ToArray();
    static void WriteBounds(byte[] model, float[] b) { for (int i = 0; i < 13; i++) BitConverter.TryWriteBytes(model.AsSpan(BoundsStart + i * 4), b[i]); }
    static float[] Union(float[] a, float[] b)
    {
        var r = (float[])a.Clone();
        for (int k = 0; k < 3; k++) { r[5 + k] = Math.Min(a[5 + k], b[5 + k]); r[8 + k] = Math.Max(a[8 + k], b[8 + k]); }
        float cx = (r[5] + r[8]) / 2, cy = (r[6] + r[9]) / 2, cz = (r[7] + r[10]) / 2;
        r[0] = cx; r[1] = cy; r[2] = cz;
        r[3] = MathF.Sqrt(MathF.Pow(r[8] - cx, 2) + MathF.Pow(r[9] - cy, 2) + MathF.Pow(r[10] - cz, 2));
        r[12] = Math.Max(a[12], b[12]);
        return r;
    }

    /// <summary>Node names of a model's ?render node table, in order.</summary>
    public static List<string> NodeNames(byte[] render)
    {
        int n = BitConverter.ToUInt16(render, 0), np = BitConverter.ToUInt16(render, 2), names = 12 + n * 120 + np * 2;
        return Enumerable.Range(0, n).Select(i =>
        {
            int o = names + BitConverter.ToInt32(render, 12 + i * 120);
            return Encoding.ASCII.GetString(render, o, Array.IndexOf(render, (byte)0, o) - o);
        }).ToList();
    }

    /// <summary>A model's bone palette (vertex bone index -> node) as "name:T/Q/S bytes" per bone, from its ?render node
    /// table: u16 node count, u16 palette count, 120-byte node records from offset 12 (u32 name offset, i32 parent,
    /// f32 T[3] Q[4] S[3] ...), the u16 palette, then the names.</summary>
    public static string Palette(byte[] render)
    {
        int n = BitConverter.ToUInt16(render, 0), np = BitConverter.ToUInt16(render, 2), names = 12 + n * 120 + np * 2;
        var sb = new StringBuilder();
        for (int i = 0; i < np; i++)
        {
            int node = BitConverter.ToUInt16(render, 12 + n * 120 + i * 2), rec = 12 + node * 120;
            int o = names + BitConverter.ToInt32(render, rec);
            sb.Append(Encoding.ASCII.GetString(render, o, Array.IndexOf(render, (byte)0, o) - o)).Append(':');
            // T/Q/S to 4 decimals (models store the same bones with -0 vs 0 and last-bit float noise)
            for (int k = 0; k < 10; k++) sb.Append((Math.Round(BitConverter.ToSingle(render, rec + 8 + k * 4), 4) + 0.0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append('|');
        }
        return sb.ToString();
    }

    /// <summary>Material slot names of a GfxMeshRes's submeshes (record u32[12] = name offset into the name table at 76 + u32@28).</summary>
    public static List<string> MeshSlots(byte[] mesh)
    {
        int nsub = BitConverter.ToInt32(mesh, 0), names = 76 + BitConverter.ToInt32(mesh, 28);
        var list = new List<string>();
        for (int i = 0; i < nsub; i++)
        {
            int o = names + BitConverter.ToInt32(mesh, 76 + i * 88 + 48);
            list.Add(Encoding.ASCII.GetString(mesh, o, Array.IndexOf(mesh, (byte)0, o) - o));
        }
        return list;
    }

    /// <summary>Build the swap, write it to <paramref name="outPath"/>, re-open and verify it; a file that fails
    /// verification is deleted (an existing file at that path is only replaced by a complete, verified one).
    /// Returns the number of checks passed.</summary>
    public static int BuildAndSave(GameInstall game, Driver target, Driver donor, string outPath, SwapLog log, SwapParts parts = SwapParts.All) =>
        BuildAndSave(game, target, PartPicks.Of(donor, parts), outPath, log);

    public static int BuildAndSave(GameInstall game, Driver target, PartPicks picks, string outPath, SwapLog log)
    {
        var res = Build(game, target, picks, log);
        var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, $".{Path.GetFileName(outPath)}.verify");
        try
        {
            var entries = HelmetSerps.Entries(game, target, picks, res.Erp, log);
            HelmetTemplate.Write(tmp, TemplateName(target, picks), entries.Select(e => (e.Entry, e.Erp)).ToList());
            log.Info($"Parts in the file: {string.Join(", ", entries.Select(e => e.Entry.Split('/')[1][8..]))}.");
            var failures = Verify(game, target, picks, tmp, out var checks);
            if (failures.Count > 0) throw new InvalidDataException("the new file failed its checks, nothing was saved:\n  " + string.Join("\n  ", failures));
            File.Move(tmp, outPath, overwrite: true);
            return checks;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static SwapResult With(this SwapResult fresh, SwapResult info)
    {
        foreach (var (k, v) in info.Materials) fresh.Materials[k] = v;
        foreach (var (k, v) in info.BindingPatches) fresh.BindingPatches[k] = v;
        foreach (var (k, v) in info.Textures) fresh.Textures[k] = v;
        foreach (var (k, v) in info.Buffers) fresh.Buffers[k] = v;
        return fresh;
    }

    /// <summary>A donor texture (SRV + surface) re-created under a private name, with its full mip chain embedded.</summary>
    static IEnumerable<ErpResource> PrivateTexture(GameInstall game, Driver donor, string texId, string privId)
    {
        var erp = ErpX.Load(donor.HelmetErp);
        var srvRes = erp.FindResource(texId);
        var dds = HelmetTemplate.FullResolution(game, srvRes);
        var probe = new ErpGfxSRVResource();
        probe.FromResource(srvRes);
        var surface = erp.FindResource(probe.Fragment0.SurfaceResourceName);
        if (surface.Fragments.Count == 3) surface.Fragments.RemoveAt(2);
        var srv = new ErpGfxSRVResource();
        srv.FromResource(srvRes);
        dds.ToErpGfxSRVResource(srv, null!, false, 0);
        surface.Identifier = privId + ".image";
        srv.Fragment0.SurfaceResourceName = surface.Identifier;
        srv.ToResource(srvRes);
        srvRes.Identifier = privId;
        ErpX.Rehash(srvRes); ErpX.Rehash(surface);
        return [srvRes, surface];
    }

    /// <summary>A name private to this (target, donor) pair, exactly as long as <paramref name="original"/>, ending like it
    /// (e.g. "_helmet_d.tif", "_visor.material"), under the target's folder.</summary>
    public static string PrivateId(string original, Driver target, Driver donor, bool model = false, string salt = "")
    {
        var file = original.Split('/')[^1];
        var tail = IsPrivateFor(original, target) ? file[file.IndexOf('_')..]                          // already private: "<hash>_visor.material"
                 : file.StartsWith(donor.Token + "_", StringComparison.Ordinal) ? file[donor.Token.Length..] : "_" + file;   // "_helmet_d.tif" / "_visor.material"
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{target.Folder}|{donor.Folder}|{original}{salt}"))).ToLowerInvariant();
        foreach (var prefix in new[] { model ? $"{target.EaidRoot}idf/hsw" : $"{target.EaidRoot}hsw/", "eaid://character_package/hsw/" })
        {
            int need = original.Length - prefix.Length - tail.Length;
            if (need < 6) continue;
            var middle = string.Concat(Enumerable.Repeat(hash, need / hash.Length + 1))[..need];
            return prefix + middle + tail;
        }
        throw new InvalidDataException($"cannot make a private name as short as {original}");
    }

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// The parts model made from two drivers (wing, visor strip and attachments each from the donor or the target):
    /// recount independently, from the game files, the triangles each surface must hold (each driver's triangles of the
    /// groups it gives, the front vents under their own surface name when they moved, the donor's shell); every buffer the
    /// new mesh reads must be a byte-identical copy of one of those drivers' buffers, every triangle read from a driver's
    /// buffers must be in a group that driver gives, every index inside its vertex buffer; the model is the target's with
    /// bounds covering the new parts, drawn far enough.
    /// </summary>
    static void VerifySplit(Driver target, Driver shellFrom, List<(Driver From, SwapParts Groups)> groupsOf, Driver attFrom, bool moveVents, bool otherShell,
        Dictionary<string, ErpResource> outIdx, Dictionary<string, ErpResource> tGame, Func<Driver, Dictionary<string, ErpResource>> gameOf,
        ErpResource oMesh, ErpResource oModel, Action<bool, string> Check)
    {
        static string Hash(ErpResource r) => Convert.ToHexString(MD5.HashData(Enumerable.Range(0, r.Fragments.Count).SelectMany(f => ErpX.Data(r, f)).ToArray()));
        Dictionary<string, ErpResource> Game(Driver d) => d == target ? tGame : gameOf(d);
        var expected = new Dictionary<string, int>();
        var origin = new Dictionary<string, (Driver From, SwapParts? Groups)>();      // buffer content -> whose, which groups
        void Count(Driver from, SwapParts? groups, string root)
        {
            var g = Game(from);
            var m = MeshDesc.Parse(ErpX.Data(g[root + "lod=0&mesh"]));
            var ibd = ErpX.Data(g[m.IndexBuffer], 1);
            bool hasWing = from.Spoiler != "no wing";
            for (int sub = 0; sub < m.Records.Count; sub++)
            {
                var pos = ErpX.Data(g[m.Streams[m.StreamsOf(sub)[0]]], 1);
                var (off, cnt) = m.IndexRange(sub); int bv = BitConverter.ToInt32(m.Records[sub], 20);
                var slot = groups is not null && moveVents && from == attFrom && m.Slots[sub] == "wing" ? HelmetParts.VentsSlot : m.Slots[sub];
                for (int t = 0; t + 2 < cnt; t += 3)
                {
                    if (groups is { } gr)
                    {
                        var v = Enumerable.Range(0, 3).Select(k => MeshDesc.Position(pos, BitConverter.ToUInt16(ibd, (off + t + k) * 2) + bv)).ToArray();
                        if (!gr.HasFlag(HelmetParts.ClassOf(m.Slots[sub], hasWing, v[0], v[1], v[2]))) continue;
                    }
                    expected[slot] = expected.GetValueOrDefault(slot) + 1;
                }
            }
            foreach (var st in m.Streams) origin.TryAdd(Hash(g[st]), (from, groups));
        }
        foreach (var (from, groups) in groupsOf) Count(from, groups, Attachments(from));
        if (otherShell) Count(shellFrom, null, ShellRoot(shellFrom.Shell!));
        string Names() => string.Join(", ", groupsOf.Select(x => x.From.Name));

        MeshDesc? o = null;
        try { o = MeshDesc.Parse(ErpX.Data(oMesh)); } catch (Exception ex) { Check(false, "helmet mesh unreadable: " + ex.Message); }
        if (o is not null)
        {
            var got = new Dictionary<string, int>();
            for (int i = 0; i < o.Records.Count; i++) got[o.Slots[i]] = got.GetValueOrDefault(o.Slots[i]) + o.IndexRange(i).Count / 3;
            Check(got.Count == expected.Count && got.All(kv => expected.GetValueOrDefault(kv.Key) == kv.Value),
                  $"helmet mesh triangles ({string.Join(", ", got.Select(kv => $"{kv.Key} {kv.Value}"))}) are not the expected " +
                  $"({string.Join(", ", expected.Select(kv => $"{kv.Key} {kv.Value}"))})");
            bool ibOk = outIdx.TryGetValue(o.IndexBuffer, out var oib) && IsPrivateFor(o.IndexBuffer, target);
            Check(ibOk && BitConverter.ToInt32(ErpX.Data(oib!, 0), 8) == ErpX.Data(oib!, 1).Length / 2, "helmet index buffer missing or its count is wrong");
            if (ibOk)
            {
                var ibd = ErpX.Data(oib!, 1);
                for (int i = 0; i < o.Records.Count; i++)
                {
                    var streams = o.StreamsOf(i).Select(k => o.Streams[k]).ToArray();
                    bool known = streams.All(st => IsPrivateFor(st, target) && outIdx.ContainsKey(st) && origin.ContainsKey(Hash(outIdx[st])));
                    Check(known, $"helmet mesh surface {i} ({o.Slots[i]}) reads a buffer that is not a copy of {Names()}'s or the shell's");
                    if (!known) continue;
                    var (from, groups) = origin[Hash(outIdx[streams[0]])];
                    bool vents = o.Slots[i] == HelmetParts.VentsSlot;
                    Check(!vents || moveVents && from == attFrom, $"helmet mesh surface {i}: front vents from {from.Name}, who isn't giving the attachments");
                    var slot = vents ? "wing" : o.Slots[i];
                    var pos = ErpX.Data(outIdx[streams[0]], 1);
                    int verts = pos.Length / 12;
                    var (off, cnt) = o.IndexRange(i); int bv = BitConverter.ToInt32(o.Records[i], 20);
                    bool inside = true, right = true;
                    for (int t = 0; t + 2 < cnt; t += 3)
                    {
                        var idx3 = Enumerable.Range(0, 3).Select(k => BitConverter.ToUInt16(ibd, (off + t + k) * 2) + bv).ToArray();
                        if (idx3.Any(v => v >= verts)) { inside = false; break; }
                        if (groups is { } gr && !gr.HasFlag(HelmetParts.ClassOf(slot, from.Spoiler != "no wing",
                                MeshDesc.Position(pos, idx3[0]), MeshDesc.Position(pos, idx3[1]), MeshDesc.Position(pos, idx3[2])))) right = false;
                    }
                    Check(inside, $"helmet mesh surface {i} ({o.Slots[i]}) indexes past its vertices");
                    Check(right, $"helmet mesh surface {i} ({o.Slots[i]}) holds triangles of {from.Name}'s that aren't {PartsLabel(groups ?? 0).ToLowerInvariant()}");
                }
            }
        }
        byte[] omod = ErpX.Data(oModel), tmod = ErpX.Data(tGame[Attachments(target) + "model"]);
        Check(omod.Length == tmod.Length && omod.AsSpan(0, BoundsStart).SequenceEqual(tmod.AsSpan(0, BoundsStart)) && omod.AsSpan(BoundsEnd).SequenceEqual(tmod.AsSpan(BoundsEnd)),
              $"helmet model is not {target.Name}'s");
        var ob = Bounds(omod);
        var sources = groupsOf.Select(x => ErpX.Data(Game(x.From)[Attachments(x.From) + "model"])).ToList();
        if (otherShell) sources.Add(ErpX.Data(Game(shellFrom)[ShellRoot(shellFrom.Shell!) + "model"]));
        foreach (var src in sources)
        {
            var sb = Bounds(src);
            Check(Enumerable.Range(0, 3).All(k => ob[5 + k] <= sb[5 + k] && ob[8 + k] >= sb[8 + k]), "helmet model bounds don't cover the new helmet");
            Check(BitConverter.ToInt32(omod, 0) == 1 && DrawDistance(omod) >= DrawDistance(src), $"helmet model is drawn to {DrawDistance(omod)}: it would vanish from far away");
        }
    }

    /// <summary>
    /// Re-open a written swap and check, independently of how it was built, that the target now resolves to exactly the
    /// donor's look for every helmet part: same material bytes (apart from the private texture names), and the donor's
    /// own full-resolution pixels behind every private texture; plus: nothing shared or donor-named is carried, every
    /// binding change is a same-length swap of a shared material for a private one, all hashes valid.
    /// </summary>
    public static List<string> Verify(GameInstall game, Driver target, Driver donor, string path, out int checks, SwapParts parts = SwapParts.All) =>
        Verify(game, target, PartPicks.Of(donor, parts), path, out checks);

    public static List<string> Verify(GameInstall game, Driver target, PartPicks picks, string path, out int checks)
    {
        var failures = new List<string>();
        int n = 0;
        void Check(bool ok, string failure) { n++; if (!ok) failures.Add(failure); }

        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        // one ERP per part (HelmetSerps), none sharing a resource
        var outIdx = HelmetSerps.ReadAll(zip, Check, out var entryOf);
        Check(outIdx.Count > 0, "no helmet ERP in the file");
        if (outIdx.Count == 0) { checks = n; return failures; }

        // what the game has
        var tGame = ErpX.Index(ErpX.Load(target.HelmetErp));
        if (File.Exists(MainErp(target)))
            foreach (var r in ErpX.Load(MainErp(target)).Resources) tGame.TryAdd(r.Identifier, r);
        // each donor's helmet as the game has it
        var dGames = new Dictionary<Driver, Dictionary<string, ErpResource>>();
        Dictionary<string, ErpResource> DGame(Driver d) => dGames.TryGetValue(d, out var i) ? i : dGames[d] = ErpX.Index(ErpX.Load(d.HelmetErp));
        Dictionary<string, ErpResource> GameOf(Driver d) => d == target ? tGame : DGame(d);
        var donors = picks.Donors;
        var parts = picks.Parts;
        var shellFrom = picks[SwapParts.Design] ?? picks.Primary!;
        var common = ErpX.Index(ErpX.Load(CommonHelmetErp(game)));
        ErpResource? Live(string id) => outIdx.TryGetValue(id, out var o) ? o : tGame.TryGetValue(id, out var g) ? g : common.GetValueOrDefault(id);

        foreach (var r in outIdx.Values)
        {
            var name = r.Identifier.Split('/')[^1];
            Check(ErpX.HashValid(r), $"{name}: wrong hash");
            Check(r.Identifier.Contains(target.Folder, StringComparison.Ordinal) || r.Identifier.StartsWith("eaid://character_package/hsw/", StringComparison.Ordinal),
                  $"{name}: not the target's own or private (would change another driver)");
            Check(!donors.Any(d => r.Identifier.Contains(d.Folder, StringComparison.Ordinal)), $"{name}: carries a donor-named resource");
        }

        // bindings: only same-length shared -> private swaps (and, in the target's own shell binding when the shell
        // changes, its shell slots -> hidden materials; checked below)
        foreach (var r in outIdx.Values.Where(r => r.ResourceType == "GfxMatBindRes"))
        {
            if (ShellSwap(target, picks) && r.Identifier == ShellBinding(target))
            {
                Check(tGame.TryGetValue(r.Identifier, out var o0) && ErpX.Data(o0).Length == ErpX.Data(r).Length, "shell binding layout changed");
                continue;
            }
            Check(tGame.TryGetValue(r.Identifier, out var orig), $"{r.Identifier.Split('/')[^1]}: not one of the target's bindings");
            if (orig is null) continue;
            var a = ErpX.Eaids(ErpX.Data(orig)).ToList(); var b = ErpX.Eaids(ErpX.Data(r)).ToList();
            Check(a.Count == b.Count && ErpX.Data(orig).Length == ErpX.Data(r).Length, $"{r.Identifier.Split('/')[^1]}: binding layout changed");
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
                if (a[i] != b[i]) Check(a[i].StartsWith(GenericRoot, StringComparison.Ordinal) && a[i].Length == b[i].Length && IsPrivateFor(b[i], target),
                                        $"binding change {a[i].Split('/')[^1]} -> {b[i].Split('/')[^1]} is not a shared -> private swap");
        }

        // per part: the target's effective material == the donor's, texture by texture
        var bindings = new Dictionary<string, ErpResource>(tGame);
        foreach (var (k, v) in outIdx) bindings[k] = v;
        var jobs = MaterialJobs(target, picks);
        foreach (var part in Parts)
        {
            string tm, dm;
            var job = jobs.FirstOrDefault(j => j.Part == part);
            if (job.Part is null)
            {
                // not swapped: bound to the same material as in the game, and that material is the game's
                try
                {
                    tm = BoundMaterial(bindings, target, part);
                    Check(tm == BoundMaterial(tGame, target, part) && !outIdx.ContainsKey(tm), $"{part}: changed, but it wasn't picked to swap");
                }
                catch (Exception ex) { Check(false, ex.Message); }
                continue;
            }
            // the part's source: the donor's same part, or (the front vents' own surface) a driver's wing material
            var (_, src, srcPart) = job;
            var sGame = GameOf(src);
            try { tm = BoundMaterial(bindings, target, part); dm = BoundMaterial(sGame, src, srcPart); }
            catch (Exception ex) { Check(false, ex.Message); continue; }
            var tMat = Live(tm);
            var dMat = dm.StartsWith(GenericRoot, StringComparison.Ordinal) ? common.GetValueOrDefault(dm) : sGame.GetValueOrDefault(dm);
            Check(tMat is not null && dMat is not null, $"{part}: material missing");
            if (tMat is null || dMat is null) continue;
            var tBytes = ErpX.Data(tMat); var dBytes = ErpX.Data(dMat);
            var tRefs = ErpX.Eaids(tBytes).ToList(); var dRefs = ErpX.Eaids(dBytes).ToList();
            Check(tRefs.Count == dRefs.Count && tBytes.Length == dBytes.Length, $"{part}: material layout differs from {src.Name}'s {srcPart}");
            if (tRefs.Count != dRefs.Count) continue;
            var back = (byte[])tBytes.Clone();
            for (int i = 0; i < tRefs.Count; i++)
            {
                if (tRefs[i] == dRefs[i]) continue;
                // a renamed reference: donor's own texture -> private texture with the donor's pixels (the target's own
                // material copied for the front vents keeps its references)
                Check(src != target && src.OwnsTexture(dRefs[i]) && IsPrivateFor(tRefs[i], target) && tRefs[i].Length == dRefs[i].Length,
                      $"{part}: reference {dRefs[i].Split('/')[^1]} -> {tRefs[i].Split('/')[^1]} is not donor -> private");
                ErpX.ReplaceEaid(back, tRefs[i], dRefs[i]);
                if (!outIdx.TryGetValue(tRefs[i], out var pSrv)) { Check(false, $"{part}: private texture {tRefs[i].Split('/')[^1]} missing"); continue; }
                var ps = new ErpGfxSRVResource(); ps.FromResource(pSrv);
                var full = HelmetTemplate.FullResolution(game, sGame[dRefs[i]]);
                Check(!ps.SurfaceRes.HasMips && ps.SurfaceRes.Width == full.header.width && ps.SurfaceRes.Height == full.header.height,
                      $"{part}: {tRefs[i].Split('/')[^1]} is not {src.Name}'s full-resolution texture");
                var dsrv = new ErpGfxSRVResource(); dsrv.FromResource(sGame[dRefs[i]]);
                Check(outIdx.TryGetValue(ps.Fragment0.SurfaceResourceName, out var pSurf) &&
                      ErpX.Data(pSurf, 1).AsSpan().EndsWith(ErpX.Data(sGame[dsrv.Fragment0.SurfaceResourceName], 1)),
                      $"{part}: {tRefs[i].Split('/')[^1]} does not hold {src.Name}'s pixels");
            }
            Check(back.AsSpan().SequenceEqual(dBytes), $"{part}: material settings differ from {src.Name}'s {srcPart}");
        }

        // helmet model: the target's attachments mesh holds, submesh by submesh, the donor's attachments (and, for a
        // different shell, the donor's shell LOD 0) with the same records, the donor's buffers under private names,
        // node groups on the same-named nodes of the target's model; the model is the target's with bounds covering it
        bool otherShell = ShellSwap(target, picks);
        bool modelChanged = (parts & ModelParts) != 0 || otherShell;
        // whose parts the mesh holds: wing, visor strip and attachments each from its donor or the target; one driver = that whole mesh
        var (wingFrom, stripFrom, attFrom) = PartsFrom(target, picks);
        var groupsOf = GroupsOf(wingFrom, stripFrom, attFrom);
        bool split = groupsOf.Count > 1;
        var partsFrom = wingFrom;
        var pGame = GameOf(partsFrom);
        var dGame = GameOf(shellFrom);                 // the shell that comes with the design
        var donor = shellFrom;
        string tA = Attachments(target), dA = Attachments(partsFrom), sR = ShellRoot(shellFrom.Shell ?? "");
        if (!modelChanged)
            // neither the spoiler nor another make's shell: the target's own helmet parts model, untouched
            Check(!outIdx.Keys.Any(k => k.StartsWith(tA, StringComparison.Ordinal) || k.Contains("/idf/hsw", StringComparison.Ordinal)), "the helmet parts model changed, but no part of it was picked");
        ErpResource? oMesh = null, oModel = null;
        if (modelChanged)
            Check(outIdx.TryGetValue(tA + "lod=0&mesh", out oMesh) & outIdx.TryGetValue(tA + "model", out oModel), "helmet model missing");
        if (split && oMesh is not null && oModel is not null)
            VerifySplit(target, shellFrom, groupsOf, attFrom, VentsFrom(target, picks) is not null, otherShell, outIdx, tGame, GameOf, oMesh, oModel, Check);
        if (!split && oMesh is not null && oModel is not null)
        {
            MeshDesc? o = null;
            try { o = MeshDesc.Parse(ErpX.Data(oMesh)); } catch (Exception ex) { Check(false, "helmet mesh unreadable: " + ex.Message); }
            var a = MeshDesc.Parse(ErpX.Data(pGame[dA + "lod=0&mesh"]));
            var srcs = Enumerable.Range(0, a.Records.Count).Select(s => (M: a, Sub: s, Base: 0)).ToList();
            int aIb = BitConverter.ToInt32(ErpX.Data(pGame[a.IndexBuffer], 0), 8);
            MeshDesc? sh = null;
            if (otherShell)
            {
                sh = MeshDesc.Parse(ErpX.Data(dGame[sR + "lod=0&mesh"]));
                srcs.AddRange(Enumerable.Range(0, sh.Records.Count).Select(s => (M: sh, Sub: s, Base: aIb)));
            }
            var tNodes = NodeNames(ErpX.Data(tGame[tA + "render"])); var dNodes = NodeNames(ErpX.Data(pGame[dA + "render"]));
            if (o is not null)
            {
                Check(o.Records.Count == srcs.Count && o.Slots.SequenceEqual(srcs.Select(x => x.M.Slots[x.Sub])),
                      $"helmet mesh surfaces ({string.Join(", ", o.Slots)}) are not {partsFrom.Name}'s");
                Check(o.Groups.Count == a.Groups.Count + (sh?.Groups.Count ?? 0), "helmet mesh node groups");
                for (int i = 0; i < Math.Min(o.Records.Count, srcs.Count); i++)
                {
                    var (m, s, b) = srcs[i];
                    byte[] or = o.Records[i], sr = m.Records[s];
                    bool same = true;
                    for (int k = 0; k < 88; k++)
                        if (!(k is >= 12 and < 16 or >= 40 and < 44 or >= 48 and < 52 or >= 56 and < 80) && or[k] != sr[k]) same = false;
                    Check(same && o.IndexRange(i).Count == m.IndexRange(s).Count && o.IndexRange(i).Offset == m.IndexRange(s).Offset + b,
                          $"helmet mesh surface {i} ({o.Slots[i]}) differs from {donor.Name}'s");
                    int[] os = o.StreamsOf(i), ss = m.StreamsOf(s);
                    for (int k = 0; k < 6; k++)
                    {
                        string on = o.Streams[os[k]], sn = m.Streams[ss[k]];
                        Check(IsPrivateFor(on, target), $"helmet mesh reads {on.Split('/')[^1]}, not a private buffer");
                        Check(outIdx.TryGetValue(on, out var ob) && (m == a ? pGame : dGame).TryGetValue(sn, out var sb) && ob.Fragments.Count == sb.Fragments.Count
                              && Enumerable.Range(0, ob.Fragments.Count).All(f => ErpX.Data(ob, f).AsSpan().SequenceEqual(ErpX.Data(sb, f))),
                              $"helmet buffer {on.Split('/')[^1]} is not {donor.Name}'s {sn.Split('/')[^1]}");
                    }
                    var expectNode = m == a ? dNodes[(int)a.Nodes[s].Node] : "helmet_attachments_x0";
                    Check(o.Nodes[i].Node < tNodes.Count && tNodes[(int)o.Nodes[i].Node] == expectNode && o.Nodes[i].Sub == i,
                          $"helmet mesh surface {i} is not on node {expectNode}");
                    Check(Parts.Contains(o.Slots[i] == "wing" ? "helmet_wing" : o.Slots[i]), $"helmet mesh uses slot '{o.Slots[i]}', which the swap doesn't carry");
                }
                // index buffer: the donor's attachments indices (then the shell's)
                byte[] want = otherShell ? [.. ErpX.Data(pGame[a.IndexBuffer], 1), .. ErpX.Data(dGame[sh!.IndexBuffer], 1)] : ErpX.Data(pGame[a.IndexBuffer], 1);
                Check(IsPrivateFor(o.IndexBuffer, target) && outIdx.TryGetValue(o.IndexBuffer, out var oib) && ErpX.Data(oib, 1).AsSpan().SequenceEqual(want)
                      && BitConverter.ToInt32(ErpX.Data(oib, 0), 8) == want.Length / 2, "helmet index buffer is not the donor's");
            }
            byte[] omod = ErpX.Data(oModel), tmod = ErpX.Data(tGame[tA + "model"]);
            Check(omod.Length == tmod.Length && omod.AsSpan(0, BoundsStart).SequenceEqual(tmod.AsSpan(0, BoundsStart)) && omod.AsSpan(BoundsEnd).SequenceEqual(tmod.AsSpan(BoundsEnd)),
                  $"helmet model is not {target.Name}'s");
            var ob2 = Bounds(omod);
            foreach (var src in otherShell ? new[] { dA + "model", sR + "model" } : [dA + "model"])
            {
                var srcData = ErpX.Data((src == dA + "model" ? pGame : dGame)[src]);
                var sb2 = Bounds(srcData);
                Check(Enumerable.Range(0, 3).All(k => ob2[5 + k] <= sb2[5 + k] && ob2[8 + k] >= sb2[8 + k]), "helmet model bounds don't cover the new helmet");
                // drawn at least as far as the parts it now holds (a shell: all its detail levels)
                Check(BitConverter.ToInt32(omod, 0) == 1 && DrawDistance(omod) >= DrawDistance(srcData),
                      $"helmet model is drawn to {DrawDistance(omod)}, {src.Split('/')[^1]} to {DrawDistance(srcData)}: it would vanish from far away");
            }
        }

        // a different shell: the target's own shell is bound, for the target only, to invisible materials
        if (otherShell)
        {
            Check(outIdx.TryGetValue(ShellBinding(target), out var se), $"{target.Name}'s shell binding missing");
            if (se is not null)
            {
                var refs = ErpX.Eaids(ErpX.Data(se)).Distinct().ToList();
                foreach (var part in ShellParts)
                {
                    var m = refs.Where(e => IsPrivateFor(e, target) && e.EndsWith($"_{part}.material", StringComparison.Ordinal)).ToList();
                    Check(m.Count == 1 && outIdx.TryGetValue(m[0], out var hm) && (HideWith == "logo"
                          ? ErpX.Data(hm).AsSpan().SequenceEqual(ErpX.Data(common[GenericRoot + "materials/helmet_logo.material"]))
                          : ErpX.Eaids(ErpX.Data(hm)).First().EndsWith("char_visor_tear_skinned.nefx2")
                            && MaterialParams.Get(ErpX.Data(hm), "me_alpha") == 0f && MaterialParams.Get(ErpX.Data(hm), "me_indexOfRefraction") == 1f
                            && MaterialParams.Get(ErpX.Data(hm), "me_roughness") == 0f), $"{target.Name}'s own shell {part} is not hidden");
                }
            }
        }

        // the layout: one folder per part, marked with the donor where the part is the donor's; every resource in its
        // part's ERP; and the whole helmet: every own texture the finished helmet uses is carried, at full resolution
        // with the game's pixels, and nothing else of the target's
        var placed = HelmetSerps.Place(game, target, outIdx, out var ownTextures);
        foreach (var (id, s) in placed)
            Check(entryOf.GetValueOrDefault(id) == HelmetSerps.EntryName(s, target, picks),
                  $"{id.Split('/')[^1]} is in {entryOf.GetValueOrDefault(id)}, not the {s.Name} ERP");
        var ownIds = ownTextures.Select(t => t.Texture).ToHashSet();
        foreach (var r in outIdx.Values.Where(r => r.ResourceType == "GfxSRVResource" && !IsPrivateFor(r.Identifier, target)))
            Check(ownIds.Contains(r.Identifier), $"{r.Identifier.Split('/')[^1]}: a texture the finished helmet doesn't use");
        foreach (var tex in ownIds)
        {
            var name = tex.Split('/')[^1];
            if (!outIdx.TryGetValue(tex, out var o)) { Check(false, $"{name}: the helmet's own texture is missing (every part must be in the file)"); continue; }
            var g = tGame[tex];
            var srv = new ErpGfxSRVResource(); srv.FromResource(o);
            var full = HelmetTemplate.FullResolution(game, g);
            Check(!srv.SurfaceRes.HasMips && srv.SurfaceRes.Width == full.header.width && srv.SurfaceRes.Height == full.header.height,
                  $"{name} is not at full resolution");
            var gsrv = new ErpGfxSRVResource(); gsrv.FromResource(g);
            Check(outIdx.TryGetValue(srv.Fragment0.SurfaceResourceName, out var os) && tGame.TryGetValue(gsrv.Fragment0.SurfaceResourceName, out var gs)
                  && ErpX.Data(os, 1).AsSpan().EndsWith(ErpX.Data(gs, 1)), $"{name} does not hold the game's pixels");
        }
        var folders = zip.Entries.Where(e => e.FullName.EndsWith(".erp", StringComparison.Ordinal)).Select(e => e.FullName[..(e.FullName.LastIndexOf('/') + 1)]).ToList();
        Check(folders.Count == folders.Distinct().Count(), "two ERPs in one part folder");
        Check(folders.Any(f => f.StartsWith("ERPs/01--00--Base", StringComparison.Ordinal)) || !ownIds.Any(), "no Base part");
        checks = n;
        return failures;
    }
}
