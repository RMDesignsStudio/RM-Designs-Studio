// Ported from Simplified Suite by Team Simplified (GPL-3.0): Simplified Driverwear Swapper/src/SimplifiedDriverwearSwapper.Core/
// Driverwear/DriverwearPrepare.cs and DriverLocator.cs
// https://github.com/TeamSimplified/simplified-suite - see NOTICE-Simplified-Suite.md
// Changes: runs in memory on the Studio's ErpX (no working folders, no texture export / import through files): each
// texture's image is moved across as it is (the donor's full mip chain written into the team's texture); MyTeam suits
// are not offered; the caller places the ERP in the template; changed resources get their hash renewed.

using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;

namespace Swapper.Core.TeamSimplified;

/// <summary>What a suit swap did, for the log and the checks.</summary>
public sealed class SuitSwapResult
{
    public required string Wear { get; init; }
    public required string Team { get; init; }
    public List<(string Name, string Gender)> Drivers { get; } = [];
    public int Lods { get; set; }
    public int Models { get; set; }
    public int Textures { get; set; }
    public int TexturesKept { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// A suit (driverwear) swap: the template team's drivers wear another set's suits. As Team Simplified's driverwear swapper
/// (whose filters were checked resource for resource against a hand-made set): the donor set's geometry (its <c>?lod=</c>
/// resources) comes as it is; its model and render resources are renamed (identifiers only) to the team's set, and once
/// more per driver for the driver's gender, under the driver's own names; the team's textures, and each driver's boots
/// and cap, keep their names and take the donor's images. One ERP, the team's driverwear.
/// </summary>
public static class SuitSwap
{
    const string Livery = "character_package/team_livery/";

    static string IdfRoot(string set) => $"{Livery}{set}/idf/";
    static string TexturesRoot(string set) => $"{Livery}{set}/textures/";

    /// <summary>The set's ERP: <c>character_package/team_livery/SET/SET.erp</c>.</summary>
    public static string SetErp(GameData game, string set) => Path.Combine(game.Assets, "character_package", "team_livery", set, set + ".erp");

    /// <summary>A team set's drivers (<c>character_package/drivers/GENDER/SET_NAME/NAME.erp</c>), male first, by name.</summary>
    public static List<(string Name, string Gender, string? Erp)> Drivers(GameData game, string set)
    {
        var list = new List<(string, string, string?)>();
        foreach (var gender in new[] { "male", "female" })
        {
            var dir = Path.Combine(game.Assets, "character_package", "drivers", gender);
            if (!Directory.Exists(dir)) continue;
            foreach (var folder in Directory.EnumerateDirectories(dir, set + "_*").Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(folder)[(set.Length + 1)..];
                if (name.Length == 0) continue;
                var erp = Path.Combine(folder, name + ".erp");
                list.Add((name, gender, File.Exists(erp) ? erp : null));
            }
        }
        return list;
    }

    /// <summary>Dress <paramref name="team"/>'s drivers in <paramref name="wear"/>'s suits; returns the driverwear ERP.</summary>
    public static (byte[] Erp, SuitSwapResult Result) Run(GameData game, string wear, string team, SwapLog log)
    {
        if (wear == team) throw new ArgumentException("those are the team's own suits");
        var wearPath = SetErp(game, wear);
        var teamPath = SetErp(game, team);
        if (!File.Exists(wearPath)) throw new FileNotFoundException($"the game has no suit set {wear} ({wearPath})");
        if (!File.Exists(teamPath)) throw new FileNotFoundException($"the game has no suit set {team} ({teamPath})");
        var result = new SuitSwapResult { Wear = wear, Team = team };
        var w = ErpX.Load(wearPath);
        // the output keeps the set file's own header; every resource is taken from a fresh read, so nothing is shared
        var output = ErpX.Load(wearPath);
        output.Resources = [];

        // the donor's geometry, as it is
        foreach (var r in w.Resources.Where(r => r.Identifier.Contains("?lod=", StringComparison.Ordinal)))
        {
            ErpX.AddOrReplace(output, r);
            result.Lods++;
        }

        // the donor's model and render resources under the team's names (identifiers only: their payloads keep pointing at
        // the donor's geometry, which comes along)
        var models = w.Resources.Where(r => r.Identifier.Contains(IdfRoot(wear), StringComparison.Ordinal)
                                            && (r.Identifier.EndsWith("?model", StringComparison.Ordinal) || r.Identifier.EndsWith("?render", StringComparison.Ordinal))).ToList();
        foreach (var r in models)
        {
            r.Identifier = r.Identifier.Replace(IdfRoot(wear), IdfRoot(team), StringComparison.Ordinal).Replace(wear + "_", team + "_", StringComparison.Ordinal);
            ErpX.AddOrReplace(output, r);
            result.Models++;
        }

        // the team's textures (and each driver's boots and cap), full size, with the donor's images
        var textures = ErpX.Load(teamPath);
        textures.Resources = textures.Resources.Where(r => r.Identifier.Contains(TexturesRoot(team), StringComparison.Ordinal)).ToList();
        var drivers = Drivers(game, team);
        if (drivers.Count == 0) result.Warnings.Add($"No drivers found for {team}: only the team's own suit models and textures are replaced.");
        foreach (var (name, gender, erp) in drivers)
        {
            result.Drivers.Add((name, gender));
            if (erp is null) { result.Warnings.Add($"{name} has no driver file: their boots and cap stay their own."); continue; }
            foreach (var r in ErpX.Load(erp).Resources.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.Identifier, $@"textures/{System.Text.RegularExpressions.Regex.Escape(name)}_(boots|cap)_")))
                ErpX.AddOrReplace(textures, r);
        }
        ChassisSwap.EmbedMipmaps(textures, game);
        var wearIndex = ErpX.Index(ErpX.Load(wearPath));
        var texIndex = ErpX.Index(textures);
        foreach (var srvRes in textures.Resources.Where(r => r.ResourceType == "GfxSRVResource").ToList())
        {
            // paired by what follows the set's (or the driver's) name: <wear>_livery_v2_d supplies <team>_livery_v2_d,
            // <wear>_boots_d supplies <driver>_boots_d
            var file = srvRes.Identifier.Split('/')[^1];
            var owner = file.StartsWith(team + "_", StringComparison.Ordinal) ? team
                      : drivers.Select(d => d.Name).FirstOrDefault(n => file.StartsWith(n + "_", StringComparison.Ordinal));
            var source = owner is null ? null : $"eaid://{TexturesRoot(wear)}{wear}_{file[(owner.Length + 1)..]}";
            if (source is null || !wearIndex.TryGetValue(source, out var donorSrv)) { result.TexturesKept++; continue; }
            var dSrv = new ErpGfxSRVResource();
            dSrv.FromResource(donorSrv);
            EgoEngineLibrary.Graphics.Dds.DdsFile dds;
            var mips = dSrv.SurfaceRes.HasMips ? ChassisSwap.OwnMipmaps(game, dSrv) : null;
            using (Stream? m = mips is null ? null : File.OpenRead(mips)) dds = dSrv.ToDdsFile(m, false, 0);
            var srv = new ErpGfxSRVResource();
            srv.FromResource(srvRes);
            dds.ToErpGfxSRVResource(srv, null!, false, 0);
            srv.ToResource(srvRes);
            ErpX.Rehash(srvRes);
            if (texIndex.TryGetValue(srv.Fragment0.SurfaceResourceName, out var surface)) ErpX.Rehash(surface);
            result.Textures++;
        }

        // per driver: the donor's model and render of the driver's gender, under the driver's names
        foreach (var (name, gender, _) in drivers)
        {
            var suffix = "_" + gender;
            var renames = new[]
            {
                (IdfRoot(wear), $"character_package/drivers/{gender}/{team}_{name}/idf/"),
                ($"{wear}_body_logos{suffix}.idf", $"{name}_body_logos.idf"),
                ($"{wear}_glove_logos{suffix}.idf", $"{name}_glove_logos.idf"),
                ($"{wear}{suffix}.idf", $"{name}.idf"),
            };
            foreach (var r in ErpX.Load(wearPath).Resources.Where(r => r.Identifier.Contains(IdfRoot(wear), StringComparison.Ordinal)
                         && r.Identifier.Contains(suffix + ".idf?", StringComparison.Ordinal)
                         && (r.Identifier.EndsWith("?model", StringComparison.Ordinal) || r.Identifier.EndsWith("?render", StringComparison.Ordinal))))
            {
                foreach (var (from, to) in renames) r.Identifier = r.Identifier.Replace(from, to, StringComparison.Ordinal);
                ErpX.AddOrReplace(output, r);
            }
        }
        foreach (var r in textures.Resources) ErpX.AddOrReplace(output, r);

        var bytes = ErpX.Save(output);
        log.Info($"Suits {wear} -> {team}: {result.Lods} geometry and {result.Models} model resources from {wear}; {result.Textures} textures with {wear}'s images" +
                 (result.TexturesKept > 0 ? $" ({result.TexturesKept} {wear} has none of: kept)" : "") +
                 $"; drivers: {(drivers.Count == 0 ? "none" : string.Join(", ", drivers.Select(d => d.Name.Replace('_', ' '))))}.");
        foreach (var warning in result.Warnings) log.Warn(warning);
        return (bytes, result);
    }

    /// <summary>
    /// The finished ERP read back and checked: it loads; it has the team's suit models (both genders) and each driver's, every
    /// one with its render; every geometry resource a render names is in it; no texture needs an external .mipmaps file.
    /// Returns the number of checks.
    /// </summary>
    public static int Verify(byte[] erpBytes, SuitSwapResult result)
    {
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) throw new InvalidDataException($"Suit check failed: {what}"); }
        ErpFile erp;
        try { erp = ErpX.Load(erpBytes); }
        catch (InvalidDataException ex) { throw new InvalidDataException("Suit check failed: the suits can't be read back: " + ex.Message, ex); }
        var ids = erp.Resources.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal);
        foreach (var g in new[] { "male", "female" })
            Check(ids.Contains($"eaid://{IdfRoot(result.Team)}{result.Team}_{g}.idf?render"), $"the team's {g} suit model is missing");
        foreach (var (name, gender) in result.Drivers)
            foreach (var part in new[] { "", "_body_logos", "_glove_logos" })
                Check(ids.Contains($"eaid://character_package/drivers/{gender}/{result.Team}_{name}/idf/{name}{part}.idf?render")
                      && ids.Contains($"eaid://character_package/drivers/{gender}/{result.Team}_{name}/idf/{name}{part}.idf?model"), $"{name}'s {part.TrimStart('_')} model is missing".Replace("'s  ", "'s "));
        foreach (var r in erp.Resources.Where(r => r.Identifier.EndsWith("?render", StringComparison.Ordinal)))
            foreach (var e in ErpX.Eaids(ErpX.Data(r)).Where(e => e.Contains("?lod=", StringComparison.Ordinal)).Distinct())
                Check(ids.Contains(e), $"{r.Identifier.Split('/')[^1]} names geometry that isn't there ({e.Split('/')[^1]})");
        foreach (var r in erp.Resources.Where(r => r.ResourceType == "GfxSRVResource"))
        {
            var srv = new ErpGfxSRVResource();
            srv.FromResource(r);
            Check(!srv.SurfaceRes.HasMips, $"{r.Identifier.Split('/')[^1]} still needs an external .mipmaps file");
        }
        return checks;
    }
}
