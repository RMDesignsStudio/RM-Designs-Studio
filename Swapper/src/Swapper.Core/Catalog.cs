using System.Text.RegularExpressions;
using Swapper.Core.TeamSimplified;

namespace Swapper.Core;

/// <summary>A car from the game that can be the chassis: a team's own 2026 car or one of its special liveries.</summary>
/// <param name="Team">The team's folder under <c>teams/</c> (its token), e.g. <c>toro_rosso</c>.</param>
/// <param name="Stem">The car file's name, e.g. <c>toro_rosso_suzuka_26</c> (the team token for the team's own car).</param>
/// <param name="Livery">A special livery's name ("Suzuka 26"), or null for the team's own car.</param>
public sealed record ChassisDonor(string Team, string Stem, string ErpPath, string? Livery, int Season = GameData.CurrentSeason)
{
    public string TeamName => GameData.TeamName(Team, Season);
    public string Label => Livery is null ? TeamName : $"{TeamName} · {Livery}";
    /// <summary>The car file's name, with the season for a 2025 car (the same names exist in both seasons): "redbull@2025".</summary>
    public string Id => Season == GameData.CurrentSeason ? Stem : $"{Stem}@{Season}";
    /// <summary>The team-colour key (the wheel donors' ids: "sauber@2025" is Kick Sauber's green).</summary>
    public string ColorKey => Season == GameData.CurrentSeason ? Team : $"{Team}@{Season}";
}

/// <summary>A set of driver suits (driverwear) from the game: <c>character_package/team_livery/&lt;Name&gt;</c>.</summary>
/// <param name="Name">The folder (and the token in its files), e.g. <c>2025-mclarensilverstone</c>.</param>
/// <param name="Key">The team part of the name, e.g. <c>mclaren</c> (<c>dams</c> for an F2 team).</param>
/// <param name="Special">A special livery's name ("Silverstone"), or null.</param>
public sealed record SuitSet(string Name, int Year, string Series, string Key, string Team, string? Special, string? ErpPath)
{
    public string Label => Special is null ? Team : $"{Team} · {Special}";
    /// <summary>The team-colour key for the chip (the character files' team slot).</summary>
    public string ColorKey => Series == "F2" ? Catalog.F2ColorKey(Key) : Key == "sauber" && Year < 2026 ? "kicksauber" : Key;
    /// <summary>Who makes the suit (see <see cref="Catalog.SuitMakers"/>), or null (Konnersport).</summary>
    public string? Maker => Catalog.SuitMakers.GetValueOrDefault(Series == "F2" ? $"{Year}-f2-{Key}" : $"{Year}-{Key}");
}

/// <summary>What the game offers for each pick: chassis (cars), steering wheels and suits.</summary>
public static class Catalog
{
    /// <summary>The steering wheels: every donor the wheel engine supports (2026 and 2025 cars).</summary>
    public static IReadOnlyList<Team> Wheels => GameData.Donors;

    /// <summary>Every car on the 2026 and the 2025 grid with its special liveries, season by season and team by team (the
    /// team's own car first). The FOM car and the My Team slots are not offered: they are not cars anyone picks to drive.</summary>
    public static List<ChassisDonor> Chassis(GameData game)
    {
        var list = new List<ChassisDonor>();
        foreach (var (season, teams) in new[] { (GameData.CurrentSeason, GameData.Teams2026), (2025, GameData.Teams2025) })
        {
            var root = Path.Combine(game.Assets, $"f1_{season}_vehicle_package", "teams");
            foreach (var team in teams.Where(t => t.Token != "fom_car").OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var cars = CarErps.InFolder(Path.Combine(root, team.Token, "wep"), team.Token)
                                  .Select(erp => new ChassisDonor(team.Token, Path.GetFileNameWithoutExtension(erp), erp, CarErps.LiveryName(erp, team.Token), season))
                                  .Where(c => Complete(game, c))
                                  .ToList();
                // two files can be one car: force_india_monaco_2026 loads force_india_monaco_26's model, the files differ
                // only in their slot's name; such a car is listed once, by the file named after its model
                foreach (var same in cars.GroupBy(c => ModelOrStem(c)))
                    list.Add(same.FirstOrDefault(c => c.Stem == same.Key) ?? same.First());
            }
        }
        return list;

        static string ModelOrStem(ChassisDonor c)
        {
            try { return ModelTokenOf(c); }
            catch (InvalidDataException) { return c.Stem; }
        }
    }

    /// <summary>
    /// Whether the game ships a car's livery in full: its paint and decal sheets keep their large levels in .mipmaps
    /// files, and those must be where the car says. The 2025 special liveries the 2026 package carries (Racing Bulls
    /// Silverstone 25, Kick Sauber Miami 25, ...) point at 2026 files the game doesn't have (the 2025 package's files of the
    /// same name are other data), so they would show low-resolution or broken; the 2025 cars themselves are complete.
    /// </summary>
    static bool Complete(GameData game, ChassisDonor car)
    {
        try
        {
            foreach (var r in ErpX.Load(car.ErpPath).Resources.Where(r => r.ResourceType == "GfxSRVResource"
                         && (r.Identifier.EndsWith("_paint_d.tif", StringComparison.Ordinal) || r.Identifier.EndsWith("_decal_da.tif", StringComparison.Ordinal))))
            {
                var srv = new EgoEngineLibrary.Archive.Erp.Data.ErpGfxSRVResource();
                srv.FromResource(r);
                if (!srv.SurfaceRes.HasMips) continue;
                var rel = srv.SurfaceRes.Frag2.MipMapFileName.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(Path.Combine(game.Assets, rel)) && !File.Exists(Path.Combine(game.GameDir, rel))) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The model token a car ERP's own car-model layer points at (a special livery is a whole car of its own).</summary>
    public static string ModelTokenOf(ChassisDonor car)
    {
        var erp = ErpX.Load(car.ErpPath);
        var layer = erp.Resources.FirstOrDefault(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"{Path.GetFileName(car.ErpPath)} has no car-model layer");
        var m = Regex.Match(System.Text.Encoding.ASCII.GetString(ErpX.Data(layer)), @"teams/[a-z0-9_]+/idf/([a-z0-9_]+)\.idf\?context=render");
        return m.Success ? m.Groups[1].Value : throw new InvalidDataException($"{Path.GetFileName(car.ErpPath)}: the car-model layer names no model");
    }

    static readonly Regex SetName = new(@"^(20\d\d)-(f2-)?([a-z0-9]+)$", RegexOptions.Compiled);

    // the F1 team part of a set name, longest first so a special livery's name can follow it ("mclarensilverstone")
    static readonly string[] F1Keys = ["konnersport", "forceindia", "tororosso", "cadillac", "mercedes", "williams", "ferrari", "mclaren",
                                       "renault", "redbull", "sauber", "zenith", "haas"];

    /// <summary>
    /// The suit maker of each team and season, F1 and F2 (special liveries are the team's), keyed by the set name without
    /// its livery. Most were confirmed by RM Designs (2026-10-17); the rest are named from the suit's cut, which the game
    /// models once per maker and gives to every team that maker dresses. Konnersport (the F1 movie's story team) has none.
    /// </summary>
    internal static readonly Dictionary<string, string> SuitMakers = new()
    {
        // F1
        ["2026-renault"] = "Alpinestars", ["2026-cadillac"] = "Alpinestars", ["2026-haas"] = "Alpinestars",
        ["2026-tororosso"] = "Alpinestars", ["2026-mclaren"] = "Alpinestars",
        ["2026-ferrari"] = "Puma", ["2026-forceindia"] = "Puma",
        ["2026-mercedes"] = "adidas", ["2026-sauber"] = "adidas",
        ["2026-williams"] = "Sparco", ["2026-redbull"] = "Sparco",
        ["2025-renault"] = "Alpinestars", ["2025-haas"] = "Alpinestars", ["2025-tororosso"] = "Alpinestars", ["2025-mclaren"] = "Alpinestars",
        ["2025-ferrari"] = "Puma", ["2025-forceindia"] = "Puma", ["2025-sauber"] = "Puma", ["2025-williams"] = "Puma",
        ["2025-mercedes"] = "adidas", ["2025-redbull"] = "Sparco", ["2025-zenith"] = "OMP",
        ["2024-renault"] = "Alpinestars", ["2024-tororosso"] = "Alpinestars", ["2024-mclaren"] = "Alpinestars", ["2024-haas"] = "Alpinestars",
        ["2024-ferrari"] = "Puma", ["2024-mercedes"] = "Puma", ["2024-forceindia"] = "Puma", ["2024-sauber"] = "Puma", ["2024-williams"] = "Puma",
        ["2024-redbull"] = "Sparco", ["2024-zenith"] = "OMP",
        // F2
        ["2026-f2-aixracing"] = "HRX", ["2026-f2-artgrandprix"] = "Puma", ["2026-f2-camposracing"] = "Sabelt", ["2026-f2-dams"] = "OMP",
        ["2026-f2-hitech"] = "Alpinestars", ["2026-f2-invictaracing"] = "Puma", ["2026-f2-mpmotorsport"] = "HRX", ["2026-f2-prema"] = "adidas",
        ["2026-f2-rodin"] = "HRX", ["2026-f2-tridentmotorsport"] = "Sparco", ["2026-f2-vanamersfoort"] = "HRX",
        ["2025-f2-aixracing"] = "HRX", ["2025-f2-artgrandprix"] = "Alpinestars", ["2025-f2-camposracing"] = "Sparco", ["2025-f2-dams"] = "OMP",
        ["2025-f2-hitech"] = "Puma", ["2025-f2-invictaracing"] = "Alpinestars", ["2025-f2-mpmotorsport"] = "HRX", ["2025-f2-prema"] = "Puma",
        ["2025-f2-rodin"] = "HRX", ["2025-f2-tridentmotorsport"] = "Sparco", ["2025-f2-vanamersfoort"] = "HRX",
        ["2024-f2-artgp"] = "Puma", ["2024-f2-camposracing"] = "Sparco", ["2024-f2-dams"] = "OMP", ["2024-f2-hitech"] = "Alpinestars",
        ["2024-f2-invictaracing"] = "Alpinestars", ["2024-f2-mpmotorsport"] = "HRX", ["2024-f2-phmracing"] = "HRX", ["2024-f2-premaracing"] = "Puma",
        ["2024-f2-rodinmotorsport"] = "HRX", ["2024-f2-trident"] = "Sparco", ["2024-f2-vanamersfoort"] = "HRX",
    };

    static readonly Dictionary<string, string> F2Names = new()
    {
        ["aixracing"] = "AIX Racing", ["artgp"] = "ART Grand Prix", ["artgrandprix"] = "ART Grand Prix", ["camposracing"] = "Campos Racing",
        ["dams"] = "DAMS", ["hitech"] = "Hitech", ["invictaracing"] = "Invicta Racing", ["mpmotorsport"] = "MP Motorsport",
        ["phmracing"] = "PHM Racing", ["prema"] = "Prema", ["premaracing"] = "Prema", ["rodin"] = "Rodin Motorsport",
        ["rodinmotorsport"] = "Rodin Motorsport", ["trident"] = "Trident", ["tridentmotorsport"] = "Trident",
        ["vanamersfoort"] = "Van Amersfoort Racing",
    };

    internal static string F2ColorKey(string key) => key switch
    {
        "artgp" => "artgrandprix", "premaracing" => "prema", "rodinmotorsport" => "rodin", "trident" => "tridentmotorsport", _ => key,
    };

    static string F1Name(string key, int year) => key switch
    {
        "forceindia" => "Aston Martin", "renault" => "Alpine", "tororosso" => "Racing Bulls", "redbull" => "Red Bull",
        "mclaren" => "McLaren", "sauber" => year >= 2026 ? "Audi" : "Kick Sauber",
        "zenith" => "APXGP",   // the F1 movie's team; "zenith" is its name in the game files
        _ => char.ToUpperInvariant(key[0]) + key[1..],
    };

    /// <summary>A suit set's name read into its season, series, team and special livery, or null when it isn't one.</summary>
    public static SuitSet? ParseSuitSet(string name, string? erpPath = null)
    {
        var m = SetName.Match(name);
        if (!m.Success) return null;
        int year = int.Parse(m.Groups[1].Value);
        var rest = m.Groups[3].Value;
        if (m.Groups[2].Success)
            return new SuitSet(name, year, "F2", rest, F2Names.GetValueOrDefault(rest, rest), null, erpPath);
        var key = F1Keys.FirstOrDefault(k => rest.StartsWith(k, StringComparison.Ordinal));
        if (key is null) return null;
        var special = rest[key.Length..];
        return new SuitSet(name, year, "F1", key, F1Name(key, year),
                           special.Length == 0 ? null : char.ToUpperInvariant(special[0]) + special[1..], erpPath);
    }

    /// <summary>Every suit set in the game (2024 to 2026, F1 and F2, special liveries, story teams), newest first.</summary>
    public static List<SuitSet> Suits(GameData game)
    {
        var dir = Path.Combine(game.Assets, "character_package", "team_livery");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir)
            .Select(d => ParseSuitSet(Path.GetFileName(d), File.Exists(Path.Combine(d, Path.GetFileName(d) + ".erp")) ? Path.Combine(d, Path.GetFileName(d) + ".erp") : null))
            .Where(s => s is { ErpPath: not null })
            .Cast<SuitSet>()
            .OrderByDescending(s => s.Year).ThenBy(s => s.Series).ThenBy(s => s.Team, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Special ?? "")
            .ToList();
    }
}
