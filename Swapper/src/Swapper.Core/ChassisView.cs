using System.IO.Compression;
using WheelSwap.Core;

namespace Swapper.Core;

/// <summary>
/// A car from the game for the viewer, with a new steering wheel built in when one is picked: the car is put in a
/// one-option template of its own (in the temp folder) and the wheel engine builds the wheel onto it in memory, the same
/// build a template's own chassis option gets. Nothing is saved.
/// </summary>
public static class ChassisView
{
    /// <summary>Why a steering wheel can't go on <paramref name="car"/> whatever the wheel (null = it can).</summary>
    public static string? WheelProblem(ChassisDonor car) =>
        !WheelBuild.WheelSeasons.Contains(car.Season) ? $"the steering wheel goes on the {string.Join(" and ", WheelBuild.WheelSeasons)} cars only (this is a {car.Season} car)" : null;

    /// <summary>
    /// Why <paramref name="wheel"/> (a wheel donor id) can't go on <paramref name="car"/> (null = it can). Besides the season:
    /// the wheel engine moves the donor's steering rig (the wheel's nodes and the driver's hand and button targets) onto the
    /// car's, so the two rigs must have the same nodes. A few cars carry an older, smaller rig (21 nodes, one brake-bias
    /// target, no gear lever: the Cadillac Miami 26 and Silverstone 26 liveries) that no wheel donor matches.
    /// </summary>
    public static string? WheelProblem(GameData game, ChassisDonor car, string wheel)
    {
        if (WheelProblem(car) is { } season) return season;
        var (tok, wSeason) = GameData.ParseDonor(wheel);
        var carRig = Rig(car.Id, () => new CarModel(game, Catalog.ModelTokenOf(car), [ErpX.Load(car.ErpPath)], car.Season));
        var wheelRig = Rig("wheel:" + wheel, () => new CarModel(game, tok, wSeason));
        return carRig.SetEquals(wheelRig) ? null : RigProblem(car, carRig.Count, wheelRig.Count);
    }

    static string RigProblem(ChassisDonor car, int carNodes, int wheelNodes) =>
        $"{car.Label} has an older steering rig than the wheels ({carNodes} nodes, the wheels {wheelNodes}), so a new wheel can't be built onto it yet";

    /// <summary><see cref="WheelProblem(GameData, ChassisDonor, string)"/> when both rigs are already known (no files read),
    /// else null.</summary>
    public static string? KnownWheelProblem(ChassisDonor car, string wheel) =>
        WheelProblem(car) ?? (Rigs.TryGetValue(car.Id, out var c) && Rigs.TryGetValue("wheel:" + wheel, out var w) && !c.SetEquals(w) ? RigProblem(car, c.Count, w.Count) : null);

    /// <summary>The wheel every rig is compared with (all wheel donors share its rig).</summary>
    const string StandardWheel = "ferrari";

    /// <summary>Why no steering wheel can go on <paramref name="car"/> (another season, or the older rig), null when wheels fit
    /// it; reads the car's model once (then remembered).</summary>
    public static string? NoWheelReason(GameData game, ChassisDonor car) => WheelProblem(game, car, StandardWheel);

    /// <summary><see cref="NoWheelReason"/> without reading any file: null when not known yet (or wheels fit).</summary>
    public static string? KnownNoWheelReason(ChassisDonor car) => KnownWheelProblem(car, StandardWheel);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<(string, string)>> Rigs = new();

    static HashSet<(string, string)> Rig(string key, Func<CarModel> model) =>
        Rigs.GetOrAdd(key, _ => NodeTable.Parse(model().Render).SteeringCluster().Keys.ToHashSet());

    public static Dictionary<string, object?> Car(PreviewScene sc, GameData game, ChassisDonor car, string? wheel)
    {
        if (wheel is null || WheelProblem(game, car, wheel) is not null)
        {
            var stock = CarView.Stock(sc, game, car);
            stock["title"] = car.Label;
            return stock;
        }
        var tpl = new SerpsTemplate(OneOption(car));
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var entry = tpl.CarEntries.Single();
        var res = tpl.BuildInMemory(game, wheel, [entry], new SwapLog(), carPlacement: true, anySeason: true);
        var c = WheelPreview.Car(sc, game, tpl, res, res.Entries[0], withCar: true);
        c["title"] = car.Label;
        c["beforeName"] = car.Label + " wheel (before)";
        return c;
    }

    /// <summary>
    /// A car with new rims (built in memory, nothing saved): the new chassis (<paramref name="newCar"/>) or the template's
    /// option (<paramref name="entry"/>), with the new steering wheel built in first when one is picked (and its rig takes it).
    /// </summary>
    public static Dictionary<string, object?> WithRims(PreviewScene sc, GameData game, SerpsTemplate? tpl, CarEntry? entry, ChassisDonor? newCar,
                                                       string? wheel, ChassisDonor rims, string title)
    {
        var log = new SwapLog();
        SerpsTemplate t;
        CarEntry e;
        var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        List<EgoEngineLibrary.Archive.Erp.ErpFile> others = [];
        if (newCar is not null)
        {
            t = new SerpsTemplate(OneOption(newCar));
            WheelBuild.MarkUnsupported(t, anySeason: true);
            e = t.CarEntries.Single();
            if (wheel is not null && WheelProblem(game, newCar, wheel) is null)
                foreach (var (k, v) in t.BuildInMemory(game, wheel, [e], log, carPlacement: true, anySeason: true).Replaced) replaced[k] = v;
        }
        else
        {
            t = tpl ?? throw new ArgumentNullException(nameof(tpl));
            e = entry ?? throw new ArgumentNullException(nameof(entry));
            if (wheel is not null && e.Problem is null)
                foreach (var (k, v) in t.BuildInMemory(game, wheel, [e], log, carPlacement: true, anySeason: true).Replaced) replaced[k] = v;
            var carFolders = t.CarEntries.Select(c => c.Folder).ToHashSet();
            static string FolderOf(string n) => n.Contains('/') ? n[..n.LastIndexOf('/')] : "";
            others = t.EntryNames.Where(n => n != e.ZipPath && n.EndsWith(".erp", StringComparison.OrdinalIgnoreCase) && (FolderOf(n) == e.Folder || !carFolders.Contains(FolderOf(n))))
                                 .Select(n => ErpX.Load(replaced.GetValueOrDefault(n) ?? t.Get(n))).ToList();
        }
        var (erp, r) = RimSwap.Run(game, replaced.GetValueOrDefault(e.ZipPath) ?? t.Get(e.ZipPath), e.Season, rims, others, log, e.Kind == EntryKind.MyTeamLivery);
        replaced[e.ZipPath] = erp;
        var c = CarView.Template(sc, game, t, e, replaced, r.ModelToken);
        c["title"] = title;
        return c;
    }

    /// <summary>The car as the only chassis option of a template (written once per car file, in the temp folder).</summary>
    static string OneOption(ChassisDonor car)
    {
        var fi = new FileInfo(car.ErpPath);
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "RM Designs Studio", "Swapper", "preview")).FullName;
        var path = Path.Combine(dir, $"{car.Id.Replace('@', '-')}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}.serps");
        if (File.Exists(path)) return path;
        var tmp = path + ".tmp";
        using (var zip = new ZipArchive(File.Create(tmp), ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry($"ERPs/02--01--Livery - {car.Label.Replace(" · ", " ")} Chassis/01--00--{car.Stem}_car_livery-and-chassis.erp", CompressionLevel.NoCompression);
            using var s = e.Open();
            using var src = File.OpenRead(car.ErpPath);
            src.CopyTo(s);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
