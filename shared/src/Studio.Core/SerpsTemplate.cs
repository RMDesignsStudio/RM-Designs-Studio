using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;

namespace Studio.Core;

public enum EntryKind { TeamChassis, FomChassis, MyTeamLivery }

public sealed class CarEntry
{
    public required string ZipPath { get; init; }
    public required EntryKind Kind { get; set; }
    public required string ModelToken { get; set; }    // model the car-model layer points at (X.idf?context=render);
                                                       // for an earlier Wheel Swapper output: the original model
    /// <summary>Set when this entry is an earlier Steering Wheel Swapper output: its private model namespaces
    /// -> the namespaces they were cloned from. A new build strips them first, so swaps never stack.</summary>
    public Dictionary<string, string>? PreviousSwap { get; set; }
    public string? SlotToken { get; init; }            // team slot of the layer (teams/<slot>/wep/...)
    public string? AnimsetId { get; init; }            // animset carried by the entry, if any
    public List<string> MatBindings { get; init; } = [];  // material binding (emb) ids the car uses
    public List<string> SteeringTextures { get; init; } = [];
    /// <summary>The car is not on its team's own model: the layer points at another model (e.g. an MCL40 or
    /// "swapcar" model) or the model's geometry comes from another car (e.g. lotus -> mercswap).</summary>
    public bool Swapped { get; set; }
    /// <summary>The season of the game car the entry is a livery for (2026, or 2025 for a template of the game's 2025
    /// cars): from the vehicle package its resources are named after ("eaid://f1_2025_vehicle_package/...").</summary>
    public int Season { get; set; } = GameData.CurrentSeason;
    public string? Problem { get; set; }
    /// <summary>The chassis' name when its file says it and no option folder does (flat templates:
    /// "myteam_livery_with_sponsors.erp" -> "MyTeam · Livery with sponsors", "konnersport_livery.erp" -> "Konnersport").</summary>
    public string? Label { get; set; }
    public string FileName => ZipPath.Split('/')[^1];
    public string Folder => ZipPath.Contains('/') ? ZipPath[..ZipPath.LastIndexOf('/')] : "";

    /// <summary>The chassis option as the template names it, from its folder: "02--01--Livery - Release Default
    /// Chassis" -> "Release Default Chassis". Null for templates without option folders.</summary>
    public string? OptionName
    {
        get
        {
            var f = Folder.Split('/')[^1];
            var m = System.Text.RegularExpressions.Regex.Match(f, @"^\d+--\d+--\s*(?:Livery\s*-\s*)?(.+)$");
            return m.Success && m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : null;
        }
    }

    /// <summary>"Release Default Chassis", "MyTeam · FOM Chassis", or without option folders "Team chassis · Williams".</summary>
    public string Title => Label ?? Kind switch
    {
        EntryKind.MyTeamLivery => "MyTeam · " + (OptionName ?? "FOM chassis"),
        EntryKind.FomChassis => OptionName ?? "FOM chassis",
        _ => OptionName ?? $"Team chassis · {GameData.TeamName(SlotToken ?? ModelToken, Season)}",
    };

    public string Describe => Title + (Swapped ? " — swapped chassis" : "") + (PreviousSwap is not null ? " — wheel already swapped" : "") + "  —  " + FileName;

}

/// <summary>A SERPs template (.serps = zip of ERPs + template.serps, zip comment = injection target).</summary>
public sealed class SerpsTemplate
{
    static readonly Regex ModelRef = new(@"teams/[a-z0-9_]+/idf/([a-z0-9_]+)\.idf\?context=render", RegexOptions.Compiled);
    static readonly Regex SlotRef = new(@"teams/([a-z0-9_]+)/wep/[a-z0-9_]+-carmodel\.layer$", RegexOptions.Compiled);
    static readonly Regex SteerMatRef = new(@"eaid://[ -~]+?steeringwheel\.material", RegexOptions.Compiled);
    static readonly HashSet<string> ModelTypes = ["ModelProxyRes", "GfxModelRes", "GfxMeshRes", "GfxBufferRes"];
    static readonly Regex IdfSuffix = new(@"/idf/([A-Za-z0-9_]+)\.idf(.*)$", RegexOptions.Compiled);
    static readonly Regex FlatLivery = new(@"^[a-z0-9]+(?:_[a-z0-9]+)*_livery(?:_[a-z0-9_]+)?\.erp$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TexTeam = new(@"/teams/([a-z0-9_]+)/textures/", RegexOptions.Compiled);

    /// <summary>A car livery ERP: "..._car_..." (Team Simplified's 1.3+ templates) or, in a flat template, "&lt;team&gt;_livery.erp",
    /// "&lt;team&gt;_livery_FOM.erp", "myteam_livery_with_sponsors.erp" (not the garage's).</summary>
    static bool IsCarErp(string name)
    {
        var file = name.Split('/')[^1];
        return name.Contains("_car_") || FlatLivery.IsMatch(file) && !file.Contains("garage", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsMyTeam(string name) =>
        name.Contains("myteam_car_livery", StringComparison.OrdinalIgnoreCase) || name.Split('/')[^1].StartsWith("myteam_livery", StringComparison.OrdinalIgnoreCase);

    static readonly Regex PackageSeason = new(@"^eaid://f1_(20\d\d)_vehicle_package/", RegexOptions.Compiled);

    /// <summary>The season of the vehicle package a car ERP's resources belong to (the car-model layer first); the
    /// current season when none says.</summary>
    static int SeasonOf(ErpFile erp, ErpResource? layer)
    {
        foreach (var id in (layer is null ? [] : new[] { layer.Identifier }).Concat(erp.Resources.Select(r => r.Identifier)))
            if (PackageSeason.Match(id) is { Success: true } m) return int.Parse(m.Groups[1].Value);
        return GameData.CurrentSeason;
    }

    public string SourcePath { get; }
    public string Name => Path.GetFileNameWithoutExtension(SourcePath);
    public string? Comment { get; }
    readonly List<(string Name, byte[] Data, bool Stored)> _entries = [];
    readonly List<string> _modelErps = [];   // template ERPs that carry model resources (swapped chassis)
    public List<CarEntry> CarEntries { get; } = [];

    public SerpsTemplate(string path)
    {
        SourcePath = path;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            Comment = zip.Comment;
            foreach (var e in zip.Entries)
            {
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                _entries.Add((e.FullName, ms.ToArray(), e.Length > 0 && e.CompressedLength == e.Length));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a readable SERPs template: the file is damaged, incomplete " +
                                           $"or not a .serps (zip) file. ({ex.Message})", ex);
        }
        var nsByErp = new Dictionary<string, Dictionary<string, HashSet<string>>>();   // car ERP -> model namespace -> its resource suffixes
        foreach (var (name, data, _) in _entries)
        {
            if (!name.EndsWith(".erp", StringComparison.OrdinalIgnoreCase) || !IsCarErp(name)) continue;
            ErpFile erp;
            CarEntry? ce;
            try
            {
                erp = ErpX.Load(data);
                ce = Inspect(name, erp);
            }
            catch (Exception ex)
            {
                // a damaged car ERP is listed (disabled) instead of silently disappearing
                if (LooksLikeCarEntry(name))
                    CarEntries.Add(new CarEntry { ZipPath = name, Kind = KindFromName(name), ModelToken = "?", Problem = $"the ERP file is damaged ({ex.Message})" });
                continue;
            }
            var ids = erp.Resources.Where(r => ModelTypes.Contains(r.ResourceType)).Select(r => r.Identifier).ToList();
            if (ids.Count > 0) _modelErps.Add(name);
            var byNs = nsByErp[name] = new();
            // which geometry each carried model uses (its ?model's LOD0 mesh namespace)
            var geo = _modelGeometry[name] = new(StringComparer.Ordinal);
            foreach (var r in erp.Resources.Where(r => r.ResourceType == "GfxModelRes" && r.Identifier.EndsWith(".idf?model", StringComparison.Ordinal)))
            {
                var ns = IdfSuffix.Match(r.Identifier).Groups[1].Value;
                var lod0 = ErpX.Eaids(ErpX.Data(r)).FirstOrDefault(e => e.EndsWith("?lod=0&mesh", StringComparison.Ordinal));
                if (lod0 is not null && IdfSuffix.Match(lod0) is { Success: true } lm) geo[ns] = lm.Groups[1].Value;
            }
            foreach (var r in erp.Resources.Where(r => ModelTypes.Contains(r.ResourceType)))
                if (IdfSuffix.Match(r.Identifier) is { Success: true } m)
                    (byNs.TryGetValue(m.Groups[1].Value, out var set) ? set : byNs[m.Groups[1].Value] = []).Add(m.Groups[2].Value);
            if (ce is not null) CarEntries.Add(ce);
        }
        BindFromPackage();
        DetectPreviousSwaps(nsByErp);
        foreach (var ce in CarEntries.Where(c => c.Problem is null)) ce.Swapped = IsSwapped(ce);
    }

    readonly Dictionary<string, Dictionary<string, string>> _modelGeometry = new();   // ERP -> model namespace -> LOD0 mesh namespace

    HashSet<string> CarEntryFolders => CarEntries.Where(c => c.Folder.Length > 0).Select(c => c.Folder).ToHashSet();

    /// <summary>
    /// Whether another template ERP's model resources apply to this car. Team Simplified templates put each chassis
    /// option in its own folder (Release / Post-Patch / FOM / a swapped chassis) and SERPs installs one of them, so an
    /// entry uses its own folder and folders that hold no chassis option; templates without option folders (all ERPs
    /// at the top level) use everything, as their swap pieces are spread over several ERPs.
    /// </summary>
    bool AppliesTo(string erpName, CarEntry ce)
    {
        var folder = erpName.Contains('/') ? erpName[..erpName.LastIndexOf('/')] : "";
        return folder == ce.Folder || !CarEntryFolders.Contains(folder);
    }

    bool IsSwapped(CarEntry ce)
    {
        var own = ce.Kind == EntryKind.TeamChassis ? ce.SlotToken ?? ce.ModelToken : "fom_car";
        if (ce.ModelToken != own) return true;                      // on another model (MCL40 chassis, "swapcar", ...)
        // the team's own model, but its geometry taken from another car (e.g. lotus -> mercswap)
        foreach (var (erp, geo) in _modelGeometry)
            if ((erp == ce.ZipPath || AppliesTo(erp, ce)) && geo.TryGetValue(ce.ModelToken, out var mesh) && mesh != ce.ModelToken
                && ce.PreviousSwap?.ContainsKey(mesh) != true)
                return true;
        return false;
    }

    bool IsPrivateOf(string erp, string ns) => CarEntries.Any(c => c.ZipPath == erp && c.PreviousSwap?.ContainsKey(ns) == true);

    static bool LooksLikeCarEntry(string name) =>
        (Regex.IsMatch(name.Split('/')[^1], @"_car_(.*livery|.*chassis|.*pkg)", RegexOptions.IgnoreCase) || IsCarErp(name)) && !name.Contains("numbers", StringComparison.OrdinalIgnoreCase);

    static EntryKind KindFromName(string name) =>
        IsMyTeam(name) ? EntryKind.MyTeamLivery
        : name.Contains("_FOM_", StringComparison.OrdinalIgnoreCase) ? EntryKind.FomChassis : EntryKind.TeamChassis;

    /// <summary>
    /// Recognise entries written by this tool: they carry a model namespace P that is a same-length private copy
    /// of another namespace C (P = C with its last 4 characters replaced by hex, see <see cref="PrivateName"/>), and
    /// the car's layer points at P. C is a 2026 game car or a namespace carried by the template.
    /// </summary>
    void DetectPreviousSwaps(Dictionary<string, Dictionary<string, HashSet<string>>> nsByErp)
    {
        var known = GameData.Teams2026.Concat(GameData.Teams2025).Select(t => t.Token).Concat(GameData.MyTeamSlots).ToHashSet();
        var carried = new Dictionary<string, HashSet<string>>();   // namespaces the template carries -> resource suffixes
        foreach (var byNs in nsByErp.Values)
            foreach (var (ns, suf) in byNs)
            {
                known.Add(ns);
                if (!carried.TryGetValue(ns, out var all)) carried[ns] = all = [];
                all.UnionWith(suf);
            }
        foreach (var ce in CarEntries.Where(c => c.Problem is null))
        {
            if (!nsByErp.TryGetValue(ce.ZipPath, out var ownNs)) continue;
            var own = ownNs.Keys.ToHashSet();
            var map = new Dictionary<string, string>();
            foreach (var p in own.Where(LooksPrivate))
            {
                var cands = known.Where(c => c != p && !own.Where(LooksPrivate).Contains(c) && PrivateLike(p, c)).ToList();
                // several names fit (e.g. merc5f74: the game's mercedes or the template's mercswap): a private copy
                // has exactly the resources of what it was cloned from, so prefer the carried namespace that matches
                if (cands.Count > 1)
                {
                    var shaped = cands.Where(c => carried.TryGetValue(c, out var suf) && suf.SetEquals(ownNs[p])).ToList();
                    if (shaped.Count == 1) cands = shaped;
                }
                if (cands.Count == 1) map[p] = cands[0];
                else if (cands.Count > 1) ce.Problem = $"this entry was written by Steering Wheel Swapper, but its original model is ambiguous ({string.Join(", ", cands)})";
            }
            bool layerIsPrivate = ce.Kind == EntryKind.MyTeamLivery ? map.Count > 0 : map.ContainsKey(ce.ModelToken);
            if (!layerIsPrivate) continue;
            ce.PreviousSwap = map;
            if (ce.Kind != EntryKind.MyTeamLivery) ce.ModelToken = map[ce.ModelToken];
            if (ce.Kind == EntryKind.TeamChassis && ce.ModelToken == "fom_car") ce.Kind = EntryKind.FomChassis;
        }
    }

    // PrivateName: names longer than 4 keep all but the last 4 characters (+4 hex); names of 4 or fewer become
    // all hex (first character 'w'/'x' when needed), e.g. haas -> w983
    static bool Hex(char ch) => ch is >= '0' and <= '9' or >= 'a' and <= 'f';

    static bool LooksPrivate(string ns) => ns.Length >= 5
        ? ns[^4..].All(Hex)
        : ns.Length > 0 && (Hex(ns[0]) || ns[0] is 'w' or 'x') && ns[1..].All(Hex);

    static bool PrivateLike(string p, string c) =>
        p.Length == c.Length && (c.Length <= 4 || p[1..^4] == c[1..^4] && (p[0] == c[0] || p[0] is 'w' or 'x'));

    public byte[] Get(string name) => _entries.First(e => e.Name == name).Data;

    /// <summary>Whether a file is stored uncompressed in the zip (SERPs templates keep some of their ERPs that way).</summary>
    public bool IsStored(string name) => _entries.FirstOrDefault(e => e.Name == name).Stored;

    /// <summary>Every zip entry path of the template.</summary>
    public IReadOnlyList<string> EntryNames => _entries.Select(e => e.Name).ToList();

    static CarEntry? Inspect(string name, ErpFile erp)
    {
        var tex = erp.Resources.Where(r => r.ResourceType == "GfxSRVResource" && WheelTextures.ChannelOf(r.Identifier) is not null)
                               .Select(r => r.Identifier).ToList();
        var layer = erp.Resources.FirstOrDefault(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal));
        var animset = erp.Resources.FirstOrDefault(r => r.ResourceType == "AnimSet")?.Identifier;
        // MyTeam first: once swapped, a MyTeam entry also carries the fom_car slot's car-model layer
        if (IsMyTeam(name))
            return new CarEntry
            {
                ZipPath = name, Kind = EntryKind.MyTeamLivery, ModelToken = "fom_car", SlotToken = "fom_car", SteeringTextures = tex, Season = SeasonOf(erp, layer),
                MatBindings = erp.Resources.Where(r => r.ResourceType == "GfxMatBindRes").Select(r => r.Identifier).ToList(),
                Label = FlatLabel(name),
            };
        if (layer is not null)
        {
            var payload = ErpX.Data(layer);
            var m = ModelRef.Match(Encoding.ASCII.GetString(payload));
            if (!m.Success) return null;
            var tok = m.Groups[1].Value;
            return new CarEntry
            {
                ZipPath = name, Kind = tok == "fom_car" ? EntryKind.FomChassis : EntryKind.TeamChassis, ModelToken = tok,
                SlotToken = SlotRef.Match(layer.Identifier) is { Success: true } s ? s.Groups[1].Value : null,
                AnimsetId = animset, SteeringTextures = tex, Season = SeasonOf(erp, layer),
                MatBindings = ErpX.Eaids(payload).Where(e => e.Contains(".emb?context=")).Distinct().ToList(),
            };
        }
        // a flat template's livery with only textures and materials (no car-model layer): the team's own chassis, bound
        // as its package ERP or the game says; textures of the FOM car (a story team such as Konnersport) are a livery
        // of the FOM car, bound like a MyTeam livery
        if (FlatLivery.IsMatch(name.Split('/')[^1]))
        {
            var team = erp.Resources.Where(r => r.ResourceType == "GfxSRVResource").Select(r => TexTeam.Match(r.Identifier)).Where(m => m.Success)
                          .GroupBy(m => m.Groups[1].Value).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            if (team is null) return null;
            var binds = erp.Resources.Where(r => r.ResourceType == "GfxMatBindRes").Select(r => r.Identifier).ToList();
            return team == "fom_car"
                ? new CarEntry { ZipPath = name, Kind = EntryKind.MyTeamLivery, ModelToken = "fom_car", SlotToken = "fom_car", SteeringTextures = tex,
                                 Season = SeasonOf(erp, null), MatBindings = binds, Label = FlatLabel(name) }
                : new CarEntry { ZipPath = name, Kind = EntryKind.TeamChassis, ModelToken = team, SlotToken = team, SteeringTextures = tex,
                                 Season = SeasonOf(erp, null), MatBindings = binds, AnimsetId = animset };
        }
        return null;
    }

    /// <summary>A flat template's chassis name from its file: "myteam_livery_with_sponsors.erp" -> "MyTeam · Livery with
    /// sponsors", "konnersport_livery.erp" -> "Konnersport"; null for a team's own (named by its kind).</summary>
    static string? FlatLabel(string name)
    {
        var file = Path.GetFileNameWithoutExtension(name.Split('/')[^1]);
        if (!FlatLivery.IsMatch(file + ".erp")) return null;
        if (file.StartsWith("myteam_", StringComparison.OrdinalIgnoreCase))
        {
            var rest = file[7..].Replace('_', ' ');
            return "MyTeam · " + char.ToUpperInvariant(rest[0]) + rest[1..];
        }
        var who = file[..file.IndexOf("_livery", StringComparison.OrdinalIgnoreCase)];
        return GameData.Teams2026.Concat(GameData.Teams2025).Any(t => t.Token == who) ? null
            : string.Join(" ", who.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>A flat template's team livery carries only textures: its car-model layer (the chassis' material
    /// bindings) is in the team's package ERP ("redbull_pkg.erp"), when the template has one.</summary>
    void BindFromPackage()
    {
        foreach (var ce in CarEntries.Where(c => c.Problem is null && c.Kind == EntryKind.TeamChassis && c.MatBindings.Count == 0))
        {
            var tok = ce.SlotToken ?? ce.ModelToken;
            var pkg = _entries.FirstOrDefault(e => e.Name.EndsWith($"{tok}_pkg.erp", StringComparison.OrdinalIgnoreCase)
                                                   && (e.Name.Contains('/') ? e.Name[..e.Name.LastIndexOf('/')] : "") == ce.Folder);
            if (pkg.Name is null) continue;
            try
            {
                var layer = ErpX.Load(pkg.Data).Resources.FirstOrDefault(r => r.Identifier.EndsWith($"/teams/{tok}/wep/{tok}-carmodel.layer", StringComparison.Ordinal));
                if (layer is null) continue;
                var payload = ErpX.Data(layer);
                if (ModelRef.Match(Encoding.ASCII.GetString(payload)) is { Success: true } m) ce.ModelToken = m.Groups[1].Value;
                ce.MatBindings.AddRange(ErpX.Eaids(payload).Where(e => e.Contains(".emb?context=")).Distinct());
            }
            catch (InvalidDataException) { }
        }
    }

    /// <summary>Same-length private name for a model namespace, stable per (namespace, donor, template).</summary>
    public string PrivateName(string ns, string donor)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{ns}|{donor}|{Name}"))).ToLowerInvariant();
        var p = ns.Length > 4 ? ns[..^4] + h[..4] : h[..ns.Length];
        if (!char.IsLetter(p[0])) p = "w" + p[1..];
        return p == ns ? "x" + p[1..] : p;
    }

    /// <summary>The car's real (pre-swap) model: game files, then every template ERP carrying model resources,
    /// the entry itself last. <paramref name="replaced"/> = ERPs already rewritten in this build.</summary>
    public CarModel LoadModel(GameData game, CarEntry ce, IReadOnlyDictionary<string, byte[]>? replaced = null, string? token = null)
    {
        ErpFile Fresh(string n) => ErpX.Load(replaced?.GetValueOrDefault(n) ?? Get(n));
        // other chassis options of the template are alternatives: they may only fill in what is missing
        var layers = _modelErps.Where(n => n != ce.ZipPath && AppliesTo(n, ce)).Select(Fresh).ToList();
        var fallback = _modelErps.Where(n => n != ce.ZipPath && !AppliesTo(n, ce)).Select(Fresh).ToList();
        // the entry itself last: when it carries a model, or a build has given it one (a My Team livery with new rims)
        if (_modelErps.Contains(ce.ZipPath) || replaced?.ContainsKey(ce.ZipPath) == true) layers.Add(Fresh(ce.ZipPath));
        return new CarModel(game, token ?? ce.ModelToken, layers, ce.Season, fallback);
    }

    /// <summary>Write the template with some ERPs replaced (zip path -> new bytes) to a new .serps; the zip comment (SERPs'
    /// injection target) and every other entry are kept as they are.</summary>
    public void Write(string outPath, IReadOnlyDictionary<string, byte[]> replaced, IReadOnlyDictionary<string, string>? renamed = null)
    {
        // written next to the target and moved into place, so a failure never leaves a half-written .serps
        var tmp = outPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (Comment is not null) zip.Comment = Comment;
                foreach (var (name, data, stored) in _entries)
                {
                    var bytes = replaced.GetValueOrDefault(name) ?? data;
                    var e = zip.CreateEntry(renamed?.GetValueOrDefault(name, name) ?? name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    if (name.EndsWith('/')) continue;
                    using var s = e.Open(); s.Write(bytes);
                }
            }
            try { File.Move(tmp, outPath, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not save {Path.GetFileName(outPath)}: it is open in another program or read-only. " +
                                      "Close it (or pick another name) and try again.", ex);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}

/// <summary>One file of a written template or project: its path, and its bytes (null = the source file <c>From</c> as it is).</summary>
public sealed record OutEntry(string Name, string? From = null, byte[]? Data = null);

/// <summary>Writing a template's files as an exact list (files added, moved or left out), for builds that add sections.</summary>
public static class SerpsWriter
{
    /// <summary>
    /// Write <paramref name="entries"/> (in that order) to a new zip at <paramref name="outPath"/> with
    /// <paramref name="comment"/> (a .serps' SERPs injection target; null for a project). A file taken from
    /// <paramref name="tpl"/> keeps its stored / compressed form. Written next to the target and moved into place, so a
    /// failure never leaves a half-written file.
    /// </summary>
    public static void Write(SerpsTemplate tpl, string outPath, IEnumerable<OutEntry> entries, string? comment)
    {
        var tmp = outPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (comment is not null) zip.Comment = comment;
                foreach (var e in entries)
                {
                    bool stored = e.From is not null && tpl.IsStored(e.From);
                    var z = zip.CreateEntry(e.Name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    if (e.Name.EndsWith('/')) continue;
                    using var s = z.Open();
                    s.Write(e.Data ?? tpl.Get(e.From ?? throw new ArgumentException($"{e.Name}: no data and no source file")));
                }
            }
            try { File.Move(tmp, outPath, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not save {Path.GetFileName(outPath)}: it is open in another program or read-only. " +
                                      "Close it (or pick another name) and try again.", ex);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}

/// <summary>Steering-wheel texture names: the layer a wheel texture is (d, n, s or the decal sheet).</summary>
public static class WheelTextures
{
    // decal map is "decals_da" on most cars, "decal_da" on Alpine / Mercedes
    static readonly Regex Channel = new(@"steering_wheel_(d|n|s|decals?_da)\.tif$", RegexOptions.Compiled);

    public static string? ChannelOf(string texId)
    {
        var m = Channel.Match(texId);
        return !m.Success ? null : m.Groups[1].Value.StartsWith("decal") ? "decals_da" : m.Groups[1].Value;
    }
}
