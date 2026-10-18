// Ported from Simplified Suite by Team Simplified (GPL-3.0): Simplified Chassis Swapper/src/SimplifiedChassisSwapper.Core/Swap/
// ChassisSwapper.cs, PkgPatcher.cs, MipmapEmbedder.cs and IncardriverLocator.cs
// https://github.com/TeamSimplified/simplified-suite - see NOTICE-Simplified-Suite.md
// Changes: runs in memory on the Studio's ErpX (no files written; the caller places the ERP in the template); the team
// ERP is the game's car for the template's team; the engine sound is always the team's (as their Swapper app does); the
// new name is chosen by NewName; changed resources get their hash renewed; the steps are logged to a SwapLog; SetAudio
// (the engine sound of a car as it is) is Swapper's, for EngineSwap; the FOM car's and My Team slots take the FOM animation set.

using System.Text;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Data.Pkg;
using EgoEngineLibrary.Graphics;

namespace Swapper.Core.TeamSimplified;

/// <summary>What a chassis swap did, for the log and the checks.</summary>
public sealed class ChassisSwapResult
{
    public required string OldName { get; init; }
    public required string NewName { get; init; }
    public required string TeamToken { get; init; }
    public int ResourcesIn { get; set; }
    public int ResourcesOut { get; set; }
    public int TexturesEmbedded { get; set; }
    public int TexturesUnreachable { get; set; }
    public int IdentifiersRenamed { get; set; }
    public int FragmentsPatched { get; set; }
    public int RemovedVtf { get; set; }
    public int RemovedWep { get; set; }
    public int LayersRebuilt { get; set; }
    public int WepCopied { get; set; }
    public string? ChassisAnimset { get; set; }
    public string? TeamAnimset { get; set; }
    public bool AnimsetTransplanted { get; set; }
    public string? EngineAudioSwitch { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// A chassis swap: a car from the game (the donor: its model, textures and cameras) re-badged under a new name, with the
/// template team's <c>/wep/</c> setup grafted on, so the game loads the donor car in that team's slot. Steps, as Team
/// Simplified's chassis swapper (whose rules were checked resource for resource against a hand-made swap):
/// <list type="number">
/// <item>the textures' external .mipmaps are put inside the ERP (before renaming: the paths must still resolve);</item>
/// <item>the donor's <c>/wep/</c> .layer payloads are kept as JSON;</item>
/// <item>the donor's name becomes the new name (same length) in every identifier and payload;</item>
/// <item>the donor's .vtf and <c>/wep/</c> resources go;</item>
/// <item>the team's <c>/wep/</c> resources come in, each .layer rebuilt from the donor's (model and material pointers -> the new
/// name, everything else -> the team; the engine sound switch is the team's);</item>
/// <item>the donor's driver animation set is published under the team's animation set id.</item>
/// </list>
/// </summary>
public static class ChassisSwap
{
    const string WepSegment = "/wep/";

    /// <summary>The game's car file for a team's slot: <c>f1_SEASON_vehicle_package/teams/T/wep/T.erp</c>.</summary>
    public static string TeamErpPath(GameData game, string teamFolder, int season) =>
        Path.Combine(game.Assets, $"f1_{season}_vehicle_package", "teams", teamFolder, "wep", teamFolder + ".erp");

    /// <summary>
    /// The name the donor is re-badged to: the same length as its own (payloads are patched in place), and its own for
    /// each donor and slot, so it can't be mistaken for any car of the game: "rm", two characters of a hash of the donor
    /// and the slot, then the slot's and the donor's letters (<c>redbull</c> into Racing Bulls -> <c>rmk3tor</c>).
    /// </summary>
    public static string NewName(ChassisDonor donor, string teamFolder)
    {
        int len = donor.Stem.Length;
        uint h = 2166136261;
        foreach (var ch in $"{donor.Id}>{teamFolder}") h = (h ^ ch) * 16777619;
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var hash = new StringBuilder();
        for (int i = 0; i < 6; i++) { hash.Append(digits[(int)(h % 36)]); h /= 36; }
        static string Letters(string s) => new(s.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var raw = "rm" + hash.ToString(0, 2) + Letters(teamFolder) + Letters(donor.Stem) + hash.ToString(2, 4) + new string('x', len);
        var name = raw[..len];
        return name == donor.Stem ? "rm" + hash.ToString(2, 4)[..Math.Min(4, len - 2)].PadRight(len - 2, 'x') : name;
    }

    /// <summary>Swap <paramref name="donor"/> into <paramref name="teamFolder"/>'s slot; returns the finished ERP.</summary>
    public static (byte[] Erp, ChassisSwapResult Result) Run(GameData game, ChassisDonor donor, string teamFolder, string newName, SwapLog log)
    {
        var oldName = donor.Stem;
        if (newName.Length != oldName.Length) throw new ArgumentException($"'{newName}' must be as long as '{oldName}'");
        if (newName == oldName) throw new ArgumentException("the new name is the donor's own");
        var teamPath = TeamErpPath(game, teamFolder, donor.Season);
        if (!File.Exists(teamPath)) throw new FileNotFoundException($"the game has no {donor.Season} car for {teamFolder} (looked for {teamPath})");
        var result = new ChassisSwapResult { OldName = oldName, NewName = newName, TeamToken = teamFolder };

        var chassis = ErpX.Load(donor.ErpPath);
        var team = ErpX.Load(teamPath);
        result.ResourcesIn = chassis.Resources.Count;

        // 1. the full-size textures inside the ERP (before renaming: the mipmaps paths must still match the files)
        EmbedMipmaps(chassis, game, result);
        if (result.TexturesUnreachable > 0)
            result.Warnings.Add($"{result.TexturesUnreachable} texture(s) had external mipmaps that couldn't be read: they keep only their small in-file image.");

        // 2. the donor's /wep/ layers, kept before they're dropped: they become the team-named layers
        var chassisLayers = CaptureLayerJson(chassis, oldName);

        // 3. the new name, in identifiers and payloads
        Rebadge(chassis, oldName, newName, result);

        // 4. the donor's own slot wiring goes
        var kept = new List<ErpResource>(chassis.Resources.Count);
        foreach (var res in chassis.Resources)
        {
            if (res.Identifier.Contains(".vtf", StringComparison.OrdinalIgnoreCase)) { result.RemovedVtf++; continue; }
            if (res.Identifier.Contains(WepSegment, StringComparison.OrdinalIgnoreCase)) { result.RemovedWep++; continue; }
            kept.Add(res);
        }
        chassis.Resources = kept;

        // 5. the team's slot wiring comes in
        GraftTeamWep(chassis, team, chassisLayers, oldName, newName, teamFolder, result);

        // 6. the donor's driver animations under the team's animation set
        TransplantAnimset(chassis, game, donor, teamPath, result);

        result.ResourcesOut = chassis.Resources.Count;
        var bytes = ErpX.Save(chassis);
        log.Info($"Chassis {donor.Label} ({oldName}) -> {teamFolder}'s slot as '{newName}': {result.ResourcesIn} -> {result.ResourcesOut} resources; " +
                 $"{result.TexturesEmbedded} textures made full size; {result.IdentifiersRenamed} names and {result.FragmentsPatched} payloads renamed; " +
                 $"{result.RemovedWep} slot resources and {result.RemovedVtf} handling files replaced by the team's ({result.LayersRebuilt} layers rebuilt, {result.WepCopied} copied).");
        log.Info(result.AnimsetTransplanted ? $"Driver animations: {result.ChassisAnimset} published as {result.TeamAnimset}." : "Driver animations: not transplanted.");
        if (result.EngineAudioSwitch is not null) log.Info($"Engine sound: {result.EngineAudioSwitch} (the team's).");
        foreach (var w in result.Warnings) log.Warn(w);
        return (bytes, result);
    }

    /// <summary>
    /// The finished ERP read back and checked: it loads; the donor's name is gone from every identifier; every slot resource
    /// of the team is there; the car-model layer points at the car under its new name, and that model is in the ERP; no
    /// texture still needs an external .mipmaps file (unless the log says it couldn't be read). Returns the number of checks.
    /// </summary>
    public static int Verify(GameData game, byte[] erpBytes, ChassisSwapResult result, int season)
    {
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) throw new InvalidDataException($"Chassis check failed: {what}"); }
        ErpFile erp;
        try { erp = ErpX.Load(erpBytes); }
        catch (InvalidDataException ex) { throw new InvalidDataException("Chassis check failed: the swapped car can't be read back: " + ex.Message, ex); }
        Check(true, "loads");
        var ids = erp.Resources.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal);
        Check(!ids.Any(i => i.Contains(result.OldName, StringComparison.Ordinal) && !i.Contains(WepSegment, StringComparison.Ordinal) && !i.EndsWith(AnimsetSuffix, StringComparison.Ordinal)),
              $"a resource still carries the donor's name '{result.OldName}'");
        var team = ErpX.Load(TeamErpPath(game, result.TeamToken, season));
        foreach (var w in team.Resources.Where(r => r.Identifier.Contains(WepSegment, StringComparison.OrdinalIgnoreCase)))
            Check(ids.Contains(w.Identifier), $"the team's {w.Identifier.Split('/')[^1]} is missing");
        var layer = erp.Resources.FirstOrDefault(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal) && r.Identifier.Contains(WepSegment, StringComparison.Ordinal));
        Check(layer is not null, "there is no car-model layer");
        var m = System.Text.RegularExpressions.Regex.Match(Encoding.ASCII.GetString(ErpX.Data(layer!)), @"teams/([a-z0-9_]+)/idf/([a-z0-9_]+)\.idf\?context=render");
        Check(m.Success && m.Groups[2].Value == result.NewName, $"the car-model layer points at '{(m.Success ? m.Groups[2].Value : "nothing")}', not the car's new name '{result.NewName}'");
        Check(ids.Contains($"eaid://f1_{season}_vehicle_package/teams/{m.Groups[1].Value}/idf/{result.NewName}.idf?render"), "the model the car-model layer points at is not in the ERP");
        int external = 0;
        foreach (var r in erp.Resources.Where(r => r.ResourceType == "GfxSRVResource"))
        {
            var srv = new ErpGfxSRVResource();
            try { srv.FromResource(r); } catch (Exception) { continue; }
            if (srv.SurfaceRes.HasMips) external++;
        }
        Check(external <= result.TexturesUnreachable, $"{external} texture(s) still need an external .mipmaps file");
        return checks;
    }

    // ------------------------------------------------------------------ mipmaps (MipmapEmbedder)

    /// <summary>Each texture's external .mipmaps chain written inside the ERP: the surface's mips fragment goes (3 -> 2) and
    /// the whole chain is stored inline, so the car is full size without the donor's files.</summary>
    static void EmbedMipmaps(ErpFile erp, GameData game, ChassisSwapResult result)
    {
        var (embedded, unreachable) = EmbedMipmaps(erp, game);
        result.TexturesEmbedded += embedded;
        result.TexturesUnreachable += unreachable;
    }

    /// <summary>A texture's own .mipmaps file (at the path it names, never another season's file of that name), or null.</summary>
    internal static string? OwnMipmaps(GameData game, ErpGfxSRVResource srv)
    {
        var rel = srv.SurfaceRes.Frag2.MipMapFileName.Replace('/', Path.DirectorySeparatorChar);
        return new[] { game.Assets, game.GameDir }.Select(root => Path.Combine(root, rel)).FirstOrDefault(File.Exists);
    }

    /// <summary>Every texture's external mip chain written inside <paramref name="erp"/>; returns how many were made full size
    /// and how many had no .mipmaps file to read.</summary>
    internal static (int Embedded, int Unreachable) EmbedMipmaps(ErpFile erp, GameData game)
    {
        int embeddedCount = 0, unreachableCount = 0;
        var index = ErpX.Index(erp);
        foreach (var res in erp.Resources.Where(r => r.ResourceType == "GfxSRVResource").ToList())
        {
            var srv = new ErpGfxSRVResource();
            try { srv.FromResource(res); }
            catch (Exception) { continue; }
            if (!srv.SurfaceRes.HasMips) continue;
            // the file at the texture's own path only (another season's file of that name is other data)
            var mipPath = OwnMipmaps(game, srv);
            bool reach = srv.SurfaceRes.HasValidMips && mipPath is not null;
            bool isArray = srv.SurfaceRes.Fragment0.ArraySize > 1;
            try
            {
                EgoEngineLibrary.Graphics.Dds.DdsFile dds;
                using (Stream? mips = reach ? File.OpenRead(mipPath!) : null)
                    dds = srv.ToDdsFile(mips, exportTexArray: isArray, texArrayIndex: 0);
                if (!index.TryGetValue(srv.Fragment0.SurfaceResourceName, out var surface)) continue;
                if (surface.Fragments.Count == 3) surface.Fragments.RemoveAt(2);
                else if (surface.Fragments.Count != 2) continue;
                var fresh = new ErpGfxSRVResource();
                fresh.FromResource(res);
                dds.ToErpGfxSRVResource(fresh, null!, isArray, 0);
                fresh.ToResource(res);
                ErpX.Rehash(res); ErpX.Rehash(surface);
                if (reach) embeddedCount++; else unreachableCount++;
            }
            catch (Exception) { /* a texture that can't be read is left as it was */ }
        }
        return (embeddedCount, unreachableCount);
    }

    // ------------------------------------------------------------------ the new name (ChassisSwapper.Rebadge)

    static void Rebadge(ErpFile erp, string oldName, string newName, ChassisSwapResult result)
    {
        foreach (var res in erp.Resources)
        {
            if (res.Identifier.Contains(oldName, StringComparison.Ordinal))
            {
                res.Identifier = res.Identifier.Replace(oldName, newName, StringComparison.Ordinal);
                result.IdentifiersRenamed++;
            }
            bool changed = false;
            foreach (var frag in res.Fragments)
            {
                byte[] data;
                try { data = frag.GetDataArray(true); }
                catch (Exception) { continue; }
                if (ErpX.ReplaceBytes(data, oldName, newName) == 0) continue;
                try { frag.SetData(data); result.FragmentsPatched++; changed = true; }
                catch (Exception) { /* a fragment that won't re-pack is left as it was */ }
            }
            if (changed) ErpX.Rehash(res);
        }
    }

    // ------------------------------------------------------------------ the team's slot wiring (ChassisSwapper.GraftTeamWep)

    /// <summary>The donor's /wep/ .layer payloads as JSON, by the part of the name after the donor's token ("-camscargame").</summary>
    static Dictionary<string, string> CaptureLayerJson(ErpFile chassis, string oldName)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var res in chassis.Resources)
        {
            if (!res.Identifier.Contains(WepSegment, StringComparison.OrdinalIgnoreCase) || !res.Identifier.EndsWith(".layer", StringComparison.OrdinalIgnoreCase) || res.Fragments.Count == 0) continue;
            try { map[SuffixAfter(res.Identifier, oldName)] = Pkg.ToJson(ErpX.Data(res)); }
            catch (Exception) { /* not a readable PKG: the team's own layer is used */ }
        }
        return map;
    }

    static string SuffixAfter(string id, string token)
    {
        var name = Path.GetFileNameWithoutExtension(id);
        int i = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? name[(i + token.Length)..] : name;
    }

    static void GraftTeamWep(ErpFile chassis, ErpFile team, Dictionary<string, string> chassisLayers, string oldName, string newName, string teamToken, ChassisSwapResult result)
    {
        foreach (var teamRes in team.Resources.ToList())
        {
            var id = teamRes.Identifier;
            if (!id.Contains(WepSegment, StringComparison.OrdinalIgnoreCase)) continue;
            if (id.EndsWith(".layer", StringComparison.OrdinalIgnoreCase) && teamRes.Fragments.Count > 0)
            {
                var suffix = SuffixAfter(id, teamToken);
                if (chassisLayers.TryGetValue(suffix, out var chassisJson))
                {
                    try
                    {
                        // the engine sound: the team's switch (only the car-model layer has one), the layer's own bundle
                        Dictionary<string, string>? audio = null;
                        try
                        {
                            if (Pkg.ReadAudioBankValues(Pkg.ToJson(ErpX.Data(teamRes))).TryGetValue(Pkg.SwitchNameKey, out var teamSwitch))
                                audio = Pkg.BuildAudioOverrides(Pkg.StripTypeTag(teamSwitch));
                        }
                        catch (Exception) { result.Warnings.Add($"The team's engine sound couldn't be read from '{suffix}': the donor's is kept."); }
                        var patched = Pkg.PatchLayerJson(chassisJson, oldName, teamToken, newName, audio);
                        teamRes.Fragments[0].SetData(Pkg.FromJson(patched));
                        ErpX.Rehash(teamRes);
                        result.LayersRebuilt++;
                        if (Pkg.ReadAudioBankValues(patched).TryGetValue(Pkg.SwitchNameKey, out var sw)) result.EngineAudioSwitch = Pkg.StripTypeTag(sw);
                    }
                    catch (Exception ex) { result.Warnings.Add($"The '{suffix}' layer couldn't be rebuilt from the donor: {ex.Message}"); }
                }
                else result.Warnings.Add($"The donor has no '{suffix}' layer: the team's own is kept, so that camera / model setup won't match the donor car.");
            }
            else result.WepCopied++;
            ErpX.AddOrReplace(chassis, teamRes);
        }
    }

    // ------------------------------------------------------------------ driver animations (ChassisSwapper.TransplantAnimset, IncardriverLocator)

    const string AnimsetSuffix = ".idf?context=animset";

    static void TransplantAnimset(ErpFile chassis, GameData game, ChassisDonor donor, string teamPath, ChassisSwapResult result)
    {
        // the donor's season: a 2025 car needs the 2025 animations
        var incPath = game.IncarDriverErpFor(donor.Season);
        if (!File.Exists(incPath)) { result.Warnings.Add($"No driver animation file ({incPath}): the in-car driver animations are left as they are."); return; }
        ErpFile inc;
        try { inc = ErpX.Load(incPath); }
        catch (Exception ex) { result.Warnings.Add($"The driver animation file couldn't be read: {ex.Message}"); return; }
        var animsets = inc.Resources.Where(r => r.Identifier.EndsWith(AnimsetSuffix, StringComparison.OrdinalIgnoreCase))
                          .Select(r => Path.GetFileName(r.Identifier[..^AnimsetSuffix.Length])).Where(n => n.Length > 0).ToList();
        // by the file's own name first, then by its team's (a special livery wears the team car's animations)
        result.ChassisAnimset = MatchAnimsetForCar(animsets, donor.ErpPath);
        // the FOM car's slot and the My Team slots name no animation set of their own: the game gives them the FOM car's
        result.TeamAnimset = MatchAnimsetForCar(animsets, teamPath) ?? FomAnimset(animsets, teamPath, donor.Season);
        if (result.ChassisAnimset is null || result.TeamAnimset is null)
        {
            result.Warnings.Add($"The driver animations couldn't be matched (donor -> {result.ChassisAnimset ?? "none"}, team -> {result.TeamAnimset ?? "none"}): they are left as they are.");
            return;
        }
        var source = inc.Resources.FirstOrDefault(r => r.Identifier.EndsWith("/" + result.ChassisAnimset + AnimsetSuffix, StringComparison.OrdinalIgnoreCase));
        var target = inc.Resources.FirstOrDefault(r => r.Identifier.EndsWith("/" + result.TeamAnimset + AnimsetSuffix, StringComparison.OrdinalIgnoreCase));
        if (source is not { Fragments.Count: > 0 } || target is not { Fragments.Count: > 0 }) return;
        try
        {
            var json = Pkg.RenameAnimsetJson(Pkg.ToJson(ErpX.Data(source)), result.ChassisAnimset, result.TeamAnimset);
            target.Fragments[0].SetData(Pkg.FromJson(json));
            ErpX.Rehash(target);
            ErpX.AddOrReplace(chassis, target);
            result.AnimsetTransplanted = true;
        }
        catch (Exception ex) { result.Warnings.Add($"The driver animations couldn't be transplanted: {ex.Message}"); }
    }

    /// <summary>For the FOM car's and the My Team slots: <c>driver_in_car_fom_YY</c> (shared by every FOM car), or null.</summary>
    static string? FomAnimset(IReadOnlyList<string> animsets, string teamPath, int season)
    {
        var slot = Path.GetFileNameWithoutExtension(teamPath);
        if (slot != "fom_car" && !slot.StartsWith("myteam_", StringComparison.Ordinal)) return null;
        var name = $"driver_in_car_fom_{season % 100:00}";
        return animsets.FirstOrDefault(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    static string? MatchAnimsetForCar(IReadOnlyList<string> animsets, string erpPath) =>
        MatchAnimset(animsets, Path.GetFileNameWithoutExtension(erpPath)) ?? (CarErps.TeamOf(erpPath) is { } team ? MatchAnimset(animsets, team) : null);

    /// <summary>The animation set for a token, by its end with separators dropped (<c>force_india</c> -> <c>...forceindia</c>), the tightest match.</summary>
    static string? MatchAnimset(IEnumerable<string> animsets, string token)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var target = Norm(token);
        if (target.Length == 0) return null;
        string? best = null;
        foreach (var a in animsets)
            if (Norm(a).EndsWith(target, StringComparison.Ordinal) && (best is null || Norm(a).Length < Norm(best).Length)) best = a;
        return best;
    }

    // ------------------------------------------------------------------ PKG payloads (PkgPatcher)

    /// <summary>PKG payloads, edited as JSON (one key per line), with the rules Team Simplified derived from a hand-made swap.
    /// Keys are matched by name: the Studio's PKG library writes them bare (<c>"mat binding"</c>), Team Simplified's with
    /// the name's hash (<c>"mat binding.ppv1.2829679144"</c>); the hash follows from the name, so both mean one key.</summary>
    internal static class Pkg
    {
        /// <summary>Keys pointing at the car's own model / material: these follow the new name (the assets travel with the car).</summary>
        static readonly string[] AssetPointerKeys = ["mat binding", "model"];

        /// <summary>The Wwise switch that alone decides which engine plays.</summary>
        public const string SwitchNameKey = "switchName";
        /// <summary>The sound bundle (per engine supplier): kept as it is, never renamed.</summary>
        public const string BundleNameKey = "bundleName";
        static readonly string[] AudioBankKeys = [SwitchNameKey, BundleNameKey];
        static readonly string[] AnimsetNameKey = ["name"];

        public static string ToJson(byte[] pkg)
        {
            using var ms = new MemoryStream(pkg, writable: false);
            var file = PkgFile.ReadPkg(ms);
            using var outMs = new MemoryStream();
            file.WriteJson(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }

        public static byte[] FromJson(string json)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var file = PkgFile.ReadJson(ms);
            using var outMs = new MemoryStream();
            file.WritePkg(outMs);
            return outMs.ToArray();
        }

        /// <summary>A donor .layer for the team's slot: the donor's name -> the team's, except the asset pointers (-> the new
        /// name) and the sound keys (never renamed; the switch replaced by <paramref name="audio"/> when given).</summary>
        public static string PatchLayerJson(string json, string oldName, string teamName, string newName, IReadOnlyDictionary<string, string>? audio)
        {
            var lines = json.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (MatchKey(lines[i], AudioBankKeys) is { } audioKey)
                {
                    if (audio is not null && audio.TryGetValue(audioKey, out var value)) lines[i] = ReplaceQuotedValue(lines[i], value);
                    continue;
                }
                bool pointer = MatchKey(lines[i], AssetPointerKeys) is not null;
                lines[i] = lines[i].Replace(oldName, pointer ? newName : teamName, StringComparison.Ordinal);
            }
            return string.Join('\n', lines);
        }

        public static Dictionary<string, string> ReadAudioBankValues(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in json.Split('\n'))
                if (MatchKey(line, AudioBankKeys) is { } key && ExtractQuotedValue(line) is { } v) map[key] = v;
            return map;
        }

        public static Dictionary<string, string> BuildAudioOverrides(string switchName) => new(StringComparer.Ordinal) { [SwitchNameKey] = "stri " + switchName };

        /// <summary>A layer with its sound keys set from <paramref name="audio"/>, nothing else changed (Swapper's engine
        /// sound swap, <see cref="EngineSwap"/>).</summary>
        public static string SetAudio(string json, IReadOnlyDictionary<string, string> audio)
        {
            var lines = json.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (MatchKey(lines[i], AudioBankKeys) is { } key && audio.TryGetValue(key, out var value)) lines[i] = ReplaceQuotedValue(lines[i], value);
            return string.Join('\n', lines);
        }

        public static string StripTypeTag(string value) => value.IndexOf(' ') is var s and >= 0 ? value[(s + 1)..] : value;

        /// <summary>An animation set renamed, its clip list still pointing at the donor's animations.</summary>
        public static string RenameAnimsetJson(string json, string from, string to)
        {
            var lines = json.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (MatchKey(lines[i], AnimsetNameKey) is not null) lines[i] = lines[i].Replace(from, to, StringComparison.Ordinal);
            return string.Join('\n', lines);
        }

        static readonly System.Text.RegularExpressions.Regex KeyOf = new(@"^\s*""([^""]+?)(?:\.ppv1\.\d+)?""\s*:", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Which of <paramref name="keys"/> a <c>"key": value</c> line is (null = none).</summary>
        static string? MatchKey(string line, string[] keys) =>
            KeyOf.Match(line) is { Success: true } m ? keys.FirstOrDefault(k => k == m.Groups[1].Value) : null;

        static string? ExtractQuotedValue(string line)
        {
            var (open, close) = ValueQuotes(line);
            return open < 0 ? null : line[(open + 1)..close];
        }

        static string ReplaceQuotedValue(string line, string value)
        {
            var (open, close) = ValueQuotes(line);
            return open < 0 ? line : string.Concat(line.AsSpan(0, open + 1), value, line.AsSpan(close));
        }

        static (int Open, int Close) ValueQuotes(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0) return (-1, -1);
            int open = line.IndexOf('"', colon + 1);
            if (open < 0) return (-1, -1);
            int close = line.LastIndexOf('"');
            return close > open ? (open, close) : (-1, -1);
        }
    }
}
