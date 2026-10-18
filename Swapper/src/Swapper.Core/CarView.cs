using System.Numerics;
using EgoEngineLibrary.Archive.Erp;
using WheelSwap.Core;

namespace Swapper.Core;

/// <summary>
/// Cars for the Studio viewer (<see cref="Viewer"/>): a template's chassis as it is, a car straight from the game, and
/// (through the wheel engine's <see cref="WheelPreview"/>) a built car with its new wheel. Each car has its body and
/// tyres, and its steering wheel on both sides (before / after) until a swap gives it another.
/// </summary>
public static class CarView
{
    /// <summary>A template's chassis option as the template has it, or as a build changed it (<paramref name="replaced"/>:
    /// zip path -> new ERP; <paramref name="token"/>: the model its car-model layer now points at).</summary>
    public static Dictionary<string, object?> Template(PreviewScene sc, GameData game, SerpsTemplate tpl, CarEntry entry,
                                                       IReadOnlyDictionary<string, byte[]>? replaced = null, string? token = null)
    {
        var model = tpl.LoadModel(game, entry, replaced, token);
        var common = sc.Common(model.Season);
        IReadOnlyDictionary<string, ErpResource> commonIx = common is null ? new Dictionary<string, ErpResource>() : ErpX.Index(common);
        // a private model (a wheel or rim build) has no game file of its own: its materials and textures are the car slot's
        IReadOnlyDictionary<string, ErpResource> slotIx = entry.SlotToken is { } slot && slot != model.Token && File.Exists(game.TeamErp(slot, model.Season))
            ? ErpX.Index(ErpX.Load(game.TeamErp(slot, model.Season))) : new Dictionary<string, ErpResource>();
        var lk = new PreviewScene.Lookup([ErpX.Index(ErpX.Load(replaced?.GetValueOrDefault(entry.ZipPath) ?? tpl.Get(entry.ZipPath))), .. OtherErps(tpl, entry), model.Res, slotIx, commonIx]);
        return Of(sc, model, lk, entry.MatBindings, commonIx, entry.Title, OwnWheel(entry));
    }

    /// <summary>A car straight from the game: a team's own car or a special livery.</summary>
    public static Dictionary<string, object?> Stock(PreviewScene sc, GameData game, ChassisDonor car)
    {
        var token = Catalog.ModelTokenOf(car);
        var erp = ErpX.Load(car.ErpPath);
        var layer = erp.Resources.First(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal));
        var binds = ErpX.Eaids(ErpX.Data(layer)).Where(e => e.Contains(".emb?context=")).Distinct().ToList();
        var model = new CarModel(game, token, [erp], car.Season);
        var common = sc.Common(model.Season);
        IReadOnlyDictionary<string, ErpResource> commonIx = common is null ? new Dictionary<string, ErpResource>() : ErpX.Index(common);
        var lk = new PreviewScene.Lookup([model.Res, commonIx]);
        return Of(sc, model, lk, binds, commonIx, car.Label, car.Label + " wheel");
    }

    static string OwnWheel(CarEntry e) =>
        (e.Kind == EntryKind.TeamChassis ? GameData.TeamName(e.SlotToken ?? e.ModelToken, e.Season) : "FOM chassis") + " wheel";

    /// <summary>The template's other ERPs that apply to this option (its own folder, or folders holding no car).</summary>
    static IEnumerable<IReadOnlyDictionary<string, ErpResource>> OtherErps(SerpsTemplate tpl, CarEntry entry)
    {
        var carFolders = tpl.CarEntries.Select(c => c.Folder).ToHashSet();
        static string FolderOf(string n) => n.Contains('/') ? n[..n.LastIndexOf('/')] : "";
        foreach (var n in tpl.EntryNames.Where(n => n != entry.ZipPath && n.EndsWith(".erp", StringComparison.OrdinalIgnoreCase))
                                        .Where(n => FolderOf(n) == entry.Folder || !carFolders.Contains(FolderOf(n)))
                                        .OrderBy(n => FolderOf(n) == entry.Folder ? 0 : 1))
        {
            IReadOnlyDictionary<string, ErpResource>? ix;
            try { ix = ErpX.Index(ErpX.Load(tpl.Get(n))); }
            catch (InvalidDataException) { ix = null; }
            if (ix is not null) yield return ix;
        }
    }

    /// <summary>A car as the viewer draws it: the body (the merged LOD0 mesh without the steering wheel parts) and tyres,
    /// and the wheel parts (both sides the same).</summary>
    static Dictionary<string, object?> Of(PreviewScene sc, CarModel model, PreviewScene.Lookup lk, IEnumerable<string> bindings,
                                          IReadOnlyDictionary<string, ErpResource> commonIx, string title, string wheelName)
    {
        var bind = PreviewScene.Binding(lk, bindings);
        var nt = NodeTable.Parse(model.Render);
        var world = PreviewScene.NodeWorld(nt);
        Func<int, Matrix4x4> bones = pal => pal >= 0 && pal < nt.Palette.Length ? world(nt.Palette[pal]) : Matrix4x4.Identity;
        var sw = nt.SteeringCluster().TryGetValue(("steering_wheel", "steer_master"), out var swn) ? swn : nt.SteerMaster;
        Matrix4x4.Invert(world(sw.Index), out var toWheel);

        var merged = model.Mesh("?lod=0&mesh");
        var parts = WheelGrafter.Family.Select(p => model.TryMesh($"?lod=0&{p}&mesh")).Where(m => m is { Empty: false }).Cast<MeshDesc>().ToList();
        var wheelRanges = parts.SelectMany(m => m.Subs.Where(s => s.ICount > 0).Select(s => (A: s.IOff, B: s.IOff + s.ICount))).OrderBy(r => r.A).ToList();
        var buf = PreviewScene.Buffers.Of(model);
        int Mat(SubMesh s) => sc.Material(lk, bind, s.Name);

        var wbox = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
        var wheel = sc.Groups(buf, parts.SelectMany(m => m.Subs.Select(s => (m, s))), Mat, bones, wbox);
        var box = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
        var body = sc.Groups(buf, merged.Subs.SelectMany(s => Outside(s, wheelRanges)).Select(s => (merged, s)), Mat, bones, box);
        body.AddRange(sc.Tyres(model.Season, nt, world, commonIx, box));
        return new()
        {
            ["title"] = title,
            ["afterName"] = wheelName,
            ["beforeName"] = wheelName,
            ["toWheel"] = Viewer.Mat(toWheel),
            ["box"] = box,
            ["wbox"] = wbox,
            ["body"] = body,
            ["after"] = wheel,
            ["before"] = wheel,
        };
    }

    /// <summary>The pieces of a submesh outside the given index ranges (whole triangles: the ranges are submesh ranges).</summary>
    static IEnumerable<SubMesh> Outside(SubMesh s, List<(int A, int B)> cut)
    {
        int at = s.IOff, end = s.IOff + s.ICount;
        foreach (var (a, b) in cut)
        {
            if (b <= at || a >= end) continue;
            if (a > at) yield return Piece(s, at, a);
            at = Math.Max(at, b);
        }
        if (at < end) yield return Piece(s, at, end);
    }

    static SubMesh Piece(SubMesh s, int from, int to) =>
        new() { Index = s.Index, Name = s.Name, IOff = from, ICount = to - from, VOff = s.VOff, Streams = s.Streams };
}
