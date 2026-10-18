using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Core;

/// <summary>An engine sound of the game: the Wwise switch a car's car-model layer names (e.g. <c>f1_m_ferrari_26</c>),
/// and the car it was found on.</summary>
public sealed record EngineSound(string Switch, string Token, int Season)
{
    /// <summary>"ferrari", "ferrari@2025", "myteam_honda": the pick's key.</summary>
    public string Id => Season == GameData.CurrentSeason ? Token : $"{Token}@{Season}";
    /// <summary>My Team's engine suppliers (Honda, Ford, ...) have sounds of their own.</summary>
    public bool MyTeam => Token.StartsWith("myteam_", StringComparison.Ordinal);
    /// <summary>The power unit maker: "Ferrari", "Mercedes", "Honda", "Audi", "RBPT Ford", "Honda RBPT", "Renault" (the FOM
    /// car's own: "FOM").</summary>
    public string Maker => Makers.TryGetValue((Token, Season), out var m) ? m
        : MyTeam ? Token["myteam_".Length..] switch { "redbull_ford" => "RBPT Ford", var s => char.ToUpperInvariant(s[0]) + s[1..] }
        : Token == "fom_car" ? "FOM" : GameData.TeamName(Token, Season);
    /// <summary>The car the sound comes from: "Haas", "Kick Sauber", "FOM chassis", "My Team". The same maker sounds a little
    /// different in each car, so the source is always shown with it.</summary>
    public string Source => MyTeam ? "My Team" : GameData.TeamName(Token, Season);
    /// <summary>"Ferrari (Haas 2026)": the log's name for it.</summary>
    public string Name => $"{Maker} ({Source} {Season})";
    /// <summary>The colour key of its chip: the maker's ("engine:Ferrari"), whichever car the sound comes from.</summary>
    public string ColorKey => "engine:" + Maker;

    /// <summary>Each team's power unit in each season (team folder, season).</summary>
    static readonly Dictionary<(string, int), string> Makers = new()
    {
        [("ferrari", 2026)] = "Ferrari", [("haas", 2026)] = "Ferrari", [("gm_cadillac", 2026)] = "Ferrari",
        [("mercedes", 2026)] = "Mercedes", [("mclaren", 2026)] = "Mercedes", [("williams", 2026)] = "Mercedes", [("lotus", 2026)] = "Mercedes",
        [("force_india", 2026)] = "Honda", [("sauber", 2026)] = "Audi",
        [("redbull", 2026)] = "RBPT Ford", [("toro_rosso", 2026)] = "RBPT Ford",
        [("ferrari", 2025)] = "Ferrari", [("haas", 2025)] = "Ferrari", [("sauber", 2025)] = "Ferrari",
        [("mercedes", 2025)] = "Mercedes", [("mclaren", 2025)] = "Mercedes", [("williams", 2025)] = "Mercedes", [("force_india", 2025)] = "Mercedes",
        [("lotus", 2025)] = "Renault", [("redbull", 2025)] = "Honda RBPT", [("toro_rosso", 2025)] = "Honda RBPT",
    };
}

/// <summary>
/// Engine sound swapping, after Team Simplified's chassis swapper (its engine choice and the PKG rules of
/// <see cref="ChassisSwap"/>): a car's sound is the one Wwise switch in its car-model layer, so a swap rewrites that switch
/// and nothing else (the layer's bundle stays the car's, as their swapper keeps it). Any engine of the 2026 and 2025 cars,
/// the FOM car's and the My Team suppliers' goes on any car of either season (the game holds every season's sounds).
/// </summary>
public static class EngineSwap
{
    /// <summary>Every engine sound in the game: each 2026 and 2025 team's (its own car's file; its special liveries share
    /// it), the FOM car's and the My Team suppliers', newest season first.</summary>
    public static List<EngineSound> Catalog(GameData game)
    {
        var list = new List<EngineSound>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var season in WheelBuild.WheelSeasons)
        {
            var teams = Path.Combine(game.Assets, $"f1_{season}_vehicle_package", "teams");
            if (!Directory.Exists(teams)) continue;
            foreach (var dir in Directory.GetDirectories(teams).OrderBy(d => d, StringComparer.Ordinal))
            {
                var token = Path.GetFileName(dir);
                var erp = Path.Combine(dir, "wep", token + ".erp");
                if (!File.Exists(erp)) continue;
                string? sw;
                try { sw = SwitchOf(ErpX.Load(erp)); }
                catch (Exception) { continue; }
                if (sw is not null && seen.Add(sw)) list.Add(new EngineSound(sw, token, season));
            }
        }
        // by maker (each maker's cars together), 2026 before 2025, then the car; the FOM car and My Team's suppliers last
        return list.OrderBy(e => e.MyTeam ? 2 : e.Token == "fom_car" ? 1 : 0).ThenBy(e => e.Maker, StringComparer.OrdinalIgnoreCase)
                   .ThenByDescending(e => e.Season).ThenBy(e => e.Source, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"Ferrari Engine (Haas 26)", "Honda Engine (My Team 25)": the ending a section with a new engine sound gets.</summary>
    public static string Suffix(EngineSound e) => $"{e.Maker} Engine ({e.Source} {e.Season % 100:00})";

    /// <summary>The engine switch an ERP's car-model layer names (null when it has none).</summary>
    public static string? SwitchOf(EgoEngineLibrary.Archive.Erp.ErpFile erp)
    {
        foreach (var r in erp.Resources.Where(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal)))
            if (ChassisSwap.Pkg.ReadAudioBankValues(ChassisSwap.Pkg.ToJson(ErpX.Data(r))).TryGetValue(ChassisSwap.Pkg.SwitchNameKey, out var v))
                return ChassisSwap.Pkg.StripTypeTag(v);
        return null;
    }

    /// <summary>Why <paramref name="ce"/> can't take an engine sound (null = it can).</summary>
    public static string? Problem(CarEntry ce) =>
        ce.Kind == EntryKind.MyTeamLivery ? "a My Team livery: its engine sound comes with the engine supplier picked in the game" : null;

    /// <summary>Give the car of <paramref name="erpBytes"/> (a chassis option's ERP) <paramref name="engine"/>'s sound.</summary>
    public static byte[] Run(byte[] erpBytes, EngineSound engine, SwapLog log)
    {
        var erp = ErpX.Load(erpBytes);
        string? was = null;
        int n = 0;
        foreach (var r in erp.Resources.Where(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal)))
        {
            var json = ChassisSwap.Pkg.ToJson(ErpX.Data(r));
            if (!ChassisSwap.Pkg.ReadAudioBankValues(json).TryGetValue(ChassisSwap.Pkg.SwitchNameKey, out var old)) continue;
            was ??= ChassisSwap.Pkg.StripTypeTag(old);
            r.Fragments[0].SetData(ChassisSwap.Pkg.FromJson(ChassisSwap.Pkg.SetAudio(json, ChassisSwap.Pkg.BuildAudioOverrides(engine.Switch))));
            ErpX.Rehash(r);
            n++;
        }
        if (n == 0) throw new InvalidDataException("the chassis has no car-model layer with an engine sound to change");
        log.Info($"Engine sound: {was} -> {engine.Switch} ({engine.Name}).");
        return ErpX.Save(erp);
    }

    /// <summary>The finished ERP read back: it loads and its car-model layer names the new engine. Returns the checks.</summary>
    public static int Verify(byte[] erpBytes, EngineSound engine)
    {
        EgoEngineLibrary.Archive.Erp.ErpFile erp;
        try { erp = ErpX.Load(erpBytes); }
        catch (InvalidDataException ex) { throw new InvalidDataException("Engine check failed: the chassis can't be read back: " + ex.Message, ex); }
        if (SwitchOf(erp) != engine.Switch) throw new InvalidDataException($"Engine check failed: the car-model layer doesn't name {engine.Switch}");
        return 2;
    }
}
