using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using Swapper.Core.TeamSimplified;

namespace Swapper.Core;

/// <summary>What making a swapped car the My Team car did, for the log and the checks.</summary>
public sealed class MyTeamChassisResult
{
    public required string NewName { get; init; }
    /// <summary>The model the slots show (the car's own, or the private copy a wheel or rims swap made).</summary>
    public required string Model { get; init; }
    public List<string> Slots { get; } = [];
    public int ContextsOwn { get; set; }
    public int ContextsNumber { get; set; }
    public int ContextsDefault { get; set; }
}

/// <summary>
/// A real car as the My Team car (worked out with RM Designs' in-game tests, 2026-10-17/18). The game spawns the My Team
/// car from the <c>fom_car</c> slot, and the six <c>myteam_ENGINE</c> slots show the same FOM car, dressed by
/// <c>fom_car.emb</c> in the binding the player's choices name. So:
/// <list type="number">
/// <item>the car is swapped into the <c>fom_car</c> slot (<see cref="ChassisSwap"/>, which also publishes the car's driver
/// animations as the FOM car's, <c>driver_in_car_fom_YY</c>), and every My Team slot's handling draws it at its own height
/// (<see cref="CarLook"/>, including the FOM car's own file in <c>teams/common.erp</c>) - the game's steps before a wheel,
/// rims or engine sound are swapped onto it, as for any new chassis;</item>
/// <item>(<see cref="Finish"/>) the six engine slots' car layers point at that car (their handling and engine sound stay
/// theirs);</item>
/// <item>the car's binding gets every binding name the FOM car's has (4,955: the My Team liveries, numbers, number fonts,
/// stickers): its own number bindings 1-99 as they are, each number font the car's number of that font, the rest its own
/// default binding. Without them the game stalls entering Career (it looks up the player's livery on the car);</item>
/// <item>the equal-cars handling file the car's layer names (<c>fom_car_fom_car.vtf</c>, which the game hasn't got) is
/// carried, as the FOM car's own.</item>
/// </list>
/// The car shows its own livery: a My Team livery is painted for the FOM car's shape and can't be put on another car.
/// </summary>
public static class MyTeamChassis
{
    /// <summary>The slot the swapped car goes into.</summary>
    public const string Slot = "fom_car";
    const string Pkg = "eaid://f1_{0}_vehicle_package/";

    /// <summary>Steps 1: the car in the fom_car slot, drawn at its own height in every My Team slot. Returns the chassis ERP
    /// (a wheel, rims or engine sound can be swapped onto it next) and the swap's result.</summary>
    public static (byte[] Erp, ChassisSwapResult Result, int Checks) Start(GameData game, ChassisDonor donor, SwapLog log)
    {
        var (erp, chassis) = ChassisSwap.Run(game, donor, Slot, ChassisSwap.NewName(donor, Slot), log);
        int checks = ChassisSwap.Verify(game, erp, chassis, donor.Season);
        foreach (var slot in GameData.MyTeamSlots)
        {
            (erp, var look) = CarLook.Apply(game, erp, donor, slot, new SwapLog());
            checks += CarLook.Verify(game, erp, donor, slot, look);
        }
        log.Info($"My Team: the car is drawn at {donor.Label}'s height in all {GameData.MyTeamSlots.Length} My Team slots.");
        return (erp, chassis, checks);
    }

    /// <summary>Steps 2-4, once the wheel / rims / engine sound are on: the engine slots, the binding names, the equal-cars
    /// handling.</summary>
    public static (byte[] Erp, MyTeamChassisResult Result) Finish(GameData game, byte[] erpBytes, string newName, int season, SwapLog log)
    {
        var pkg = string.Format(Pkg, season);
        var erp = ErpX.Load(erpBytes);
        var main = erp.Resources.FirstOrDefault(r => r.Identifier.EndsWith($"/{Slot}-carmodel.layer", StringComparison.Ordinal))
                   ?? throw new InvalidDataException("the swapped car has no fom_car car-model layer");
        var model = ErpX.Eaids(ErpX.Data(main)).FirstOrDefault(e => e.EndsWith(".idf?context=render", StringComparison.Ordinal))
                    ?? throw new InvalidDataException("the swapped car's layer names no model");
        var modelPath = model[pkg.Length..^"?context=render".Length];            // teams/NEW/idf/MODEL.idf
        var bindingPath = $"teams/{newName}/idf/{newName}.emb";
        var result = new MyTeamChassisResult { NewName = newName, Model = modelPath };

        // 2. the engine slots: their own car layers (handling, engine sound), showing the car
        foreach (var slot in GameData.MyTeamSlots.Where(s => s != Slot))
        {
            var path = game.TeamErp(slot, season);
            if (!File.Exists(path)) throw new FileNotFoundException($"the game has no {season} car for {slot}", path);
            foreach (var r in ErpX.Load(path).Resources.Where(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal)
                                                                   || r.Identifier.EndsWith("-carshowroommodel.layer", StringComparison.Ordinal)))
            {
                var d = ErpX.Data(r);
                int m = ErpX.ReplaceInEaids(d, "teams/fom_car/idf/fom_car.idf", modelPath);
                int b = ErpX.ReplaceInEaids(d, "teams/fom_car/idf/fom_car.emb", bindingPath);
                if (m == 0 || b == 0) throw new InvalidDataException($"{r.Identifier.Split('/')[^1]} doesn't name the FOM car as expected");
                r.Fragments[0].SetData(d, true);
                ErpX.Rehash(r);
                ErpX.AddOrReplace(erp, r);
            }
            result.Slots.Add(slot);
        }

        // 3. every binding name of the FOM car's on the car's binding
        var mine = $"{pkg}{bindingPath}?context=";
        var have = erp.Resources.Select(r => r.Identifier).Where(i => i.StartsWith(mine, StringComparison.Ordinal)).Select(i => i[mine.Length..]).ToHashSet();
        if (!have.Contains("default")) throw new InvalidDataException("the swapped car has no default binding");
        var fom = $"{pkg}teams/fom_car/idf/fom_car.emb?context=";
        var contexts = FomBindingNames(game, season, fom).Select(i => i[fom.Length..]).ToList();
        var snapshot = ErpX.Save(erp);
        var single = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        ErpResource Copy(string context)
        {
            if (!single.TryGetValue(context, out var one))
            {
                var e = ErpX.Load(snapshot);
                e.Resources = [e.Resources.First(r => r.Identifier == mine + context)];
                single[context] = one = ErpX.Save(e);
            }
            return ErpX.Load(one).Resources[0];
        }
        foreach (var c in contexts)
        {
            if (have.Contains(c)) { result.ContextsOwn++; continue; }
            var n = Regex.Match(c, @"^number_set_.+_(\d+)$");
            var from = n.Success && have.Contains(n.Groups[1].Value) ? n.Groups[1].Value : "default";
            if (from == "default") result.ContextsDefault++; else result.ContextsNumber++;
            var r = Copy(from);
            r.Identifier = mine + c;
            ErpX.AddOrReplace(erp, r);
        }

        // 4. the equal-cars handling the car's layer names: the FOM car's own (drawn at the car's height by Start)
        var baseVtf = ErpX.Eaids(ErpX.Data(main)).FirstOrDefault(e => e.Contains("/handling/", StringComparison.Ordinal) && e.EndsWith(".vtf", StringComparison.Ordinal)
                                                                         && e.Contains("fom_car_fom_car", StringComparison.Ordinal));
        var fomVtf = erp.Resources.FirstOrDefault(r => r.Identifier == $"{pkg}teams/fom_car/handling/fom_car.vtf");
        if (baseVtf is not null && fomVtf is not null && !erp.Resources.Any(r => r.Identifier == baseVtf))
        {
            var e = ErpX.Load(ErpX.Save(erp));
            e.Resources = [e.Resources.First(r => r.Identifier == fomVtf.Identifier)];
            var copy = ErpX.Load(ErpX.Save(e)).Resources[0];
            copy.Identifier = baseVtf;
            ErpX.AddOrReplace(erp, copy);
        }

        log.Info($"My Team: the {result.Slots.Count} engine slots show the car ({modelPath.Split('/')[^1]}); its binding has the FOM car's " +
                 $"{contexts.Count} binding names ({result.ContextsOwn} its own, {result.ContextsNumber} number fonts with its numbers, " +
                 $"{result.ContextsDefault} its default livery).");
        return (ErpX.Save(erp), result);
    }

    /// <summary>Every binding name of the FOM car's (<paramref name="prefix"/> + context): in its own car file (2026), and in
    /// the season's base file <c>teams/common_fom.erp</c>, where the 2025 FOM car keeps its model, bindings, materials and
    /// textures (its own file has only the slot layers).</summary>
    static IEnumerable<string> FomBindingNames(GameData game, int season, string prefix)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { game.TeamErp(Slot, season), game.CommonFomErp(season) })
            if (File.Exists(path))
                foreach (var r in ErpX.Load(path).Resources)
                    if (r.Identifier.StartsWith(prefix, StringComparison.Ordinal)) names.Add(r.Identifier);
        return names;
    }

    /// <summary>The finished ERP read back: every My Team slot shows the car, which is in the ERP; every binding name of
    /// the FOM car's is on the car's binding. Returns the number of checks.</summary>
    public static int Verify(GameData game, byte[] erpBytes, MyTeamChassisResult result, int season)
    {
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) throw new InvalidDataException($"My Team chassis check failed: {what}"); }
        var pkg = string.Format(Pkg, season);
        var erp = ErpX.Load(erpBytes);
        var ids = erp.Resources.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal);
        Check(ids.Contains($"{pkg}{result.Model}?context=render") || erp.Resources.Any(r => r.Identifier.StartsWith($"{pkg}{result.Model}?", StringComparison.Ordinal)),
              $"the model {result.Model} isn't in the chassis");
        foreach (var slot in GameData.MyTeamSlots)
        {
            var layer = erp.Resources.FirstOrDefault(r => r.Identifier.EndsWith($"/{slot}-carmodel.layer", StringComparison.Ordinal));
            Check(layer is not null, $"{slot} has no car layer");
            Check(ErpX.Eaids(ErpX.Data(layer!)).Any(e => e == $"{pkg}{result.Model}?context=render"), $"{slot} doesn't show the car");
        }
        var mine = $"{pkg}teams/{result.NewName}/idf/{result.NewName}.emb?context=";
        var fom = $"{pkg}teams/fom_car/idf/fom_car.emb?context=";
        var missing = FomBindingNames(game, season, fom).Count(i => !ids.Contains(mine + i[fom.Length..]));
        Check(missing == 0, $"{missing} of the FOM car's binding names aren't on the car's binding");
        return checks;
    }
}
