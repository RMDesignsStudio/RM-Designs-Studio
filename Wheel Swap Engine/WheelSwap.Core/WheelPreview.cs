using System.Numerics;
using EgoEngineLibrary.Archive.Erp;

namespace WheelSwap.Core;

/// <summary>
/// An interactive 3D preview of a build: the new wheel (and the car's wheel before, for comparison) and, on the
/// preview page, the whole car around it. Real geometry, the materials the car's material binding gives each slot and
/// their textures, drawn by the RM Designs Studio viewer (Studio.Core <see cref="Viewer"/>, shared with the other
/// Studio apps). A self-contained WebGL2 page: no internet, no libraries.
/// </summary>
public static class WheelPreview
{
    /// <summary>A full page for every car of a build, with a Steering wheel / Car toggle.</summary>
    public static string Page(GameData game, SerpsTemplate tpl, BuildResult res, int textureSize = 1024)
    {
        var sc = new PreviewScene(game) { TexSize = textureSize, PaintSize = textureSize * 2 };
        var cars = res.Entries.Select(be => Car(sc, game, tpl, res, be, withCar: true)).ToList();
        // two cars of one option (MyTeam's paints share a folder): name them by their file, e.g. "MyTeam · FOM Chassis · azure"
        for (int i = 0; i < cars.Count; i++)
            if (cars.Count(c => (string)c["title"]! == (string)cars[i]["title"]!) > 1)
                cars[i]["title"] += " · " + FileLabel(res.Entries[i].Entry.FileName);
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Wheel Preview</title><style>{{Viewer.Css}}</style></head><body class="page">
            <div class="band"><div class="band-in"><b>Wheel preview</b><span>{{Viewer.E(GameData.DonorName(res.DonorToken))}} wheel → {{Viewer.E(tpl.Name)}}</span></div></div>
            <main>{{Viewer.Markup("pv", cars, new Viewer.Options(), sc)}}
            <p class="note">*an approximation of the in-game look: real geometry, materials and textures, simplified lighting · drag to turn, scroll to zoom, right-drag to pan</p>
            </main></body></html>
            """;
    }

    /// <summary>The wheel-only viewer block (canvas, controls, data and script) for one car; embeddable in the report.</summary>
    public static string Section(GameData game, SerpsTemplate tpl, BuildResult res, BuiltEntry be, string id, int textureSize = 512)
    {
        var sc = new PreviewScene(game) { TexSize = textureSize, PaintSize = textureSize };
        return Viewer.Markup(id, [Car(sc, game, tpl, res, be, withCar: false)], new Viewer.Options(), sc);
    }

    // ---------------------------------------------------------------- data

    /// <summary>One built car for the viewer: its body and tyres (<paramref name="withCar"/>) and the new wheel after, the car's own wheel before.</summary>
    public static Dictionary<string, object?> Car(PreviewScene sc, GameData game, SerpsTemplate tpl, BuildResult res, BuiltEntry be, bool withCar)
    {
        var outErp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        var target = tpl.LoadModel(game, be.Entry);
        var after = new CarModel(game, be.PrivateToken, [outErp]);
        var common = sc.Common(target.Season);
        IReadOnlyDictionary<string, ErpResource> commonIx = common is null ? new Dictionary<string, ErpResource>() : ErpX.Index(common);
        // materials and textures: the entry, then the template's other ERPs that apply to this option (its own folder, or
        // folders holding no car), then the model's game files and the shared vehicle resources
        var carFolders = tpl.CarEntries.Select(c => c.Folder).ToHashSet();
        static string FolderOf(string n) => n.Contains('/') ? n[..n.LastIndexOf('/')] : "";
        var others = tpl.EntryNames.Where(n => n != be.Entry.ZipPath && n.EndsWith(".erp", StringComparison.OrdinalIgnoreCase))
                        .Where(n => FolderOf(n) == be.Entry.Folder || !carFolders.Contains(FolderOf(n)))
                        .OrderBy(n => FolderOf(n) == be.Entry.Folder ? 0 : 1)
                        .Select(n => { try { return ErpX.Index(ErpX.Load(tpl.Get(n))); } catch (InvalidDataException) { return null; } })
                        .Where(x => x is not null).Cast<IReadOnlyDictionary<string, ErpResource>>().ToList();
        IReadOnlyDictionary<string, ErpResource> origIx = ErpX.Index(ErpX.Load(tpl.Get(be.Entry.ZipPath)));
        var lkBefore = new PreviewScene.Lookup([origIx, .. others, target.Res, commonIx]);
        var lkAfter = new PreviewScene.Lookup([ErpX.Index(outErp), origIx, .. others, target.Res, commonIx]);
        var bindBefore = PreviewScene.Binding(lkBefore, be.Entry.MatBindings);
        var bindAfter = PreviewScene.Binding(lkAfter, be.Entry.MatBindings);

        var ntAfter = NodeTable.Parse(after.Render);
        var wAfter = PreviewScene.NodeWorld(ntAfter);
        Func<int, Matrix4x4> Bones(NodeTable nt, Func<int, Matrix4x4> w) =>
            pal => pal >= 0 && pal < nt.Palette.Length ? w(nt.Palette[pal]) : Matrix4x4.Identity;
        var ntBefore = NodeTable.Parse(target.Render);
        var sw = ntAfter.SteeringCluster().TryGetValue(("steering_wheel", "steer_master"), out var swn) ? swn : ntAfter.SteerMaster;
        Matrix4x4.Invert(wAfter(sw.Index), out var toWheel);

        int origIb = target.Buf(target.Mesh("?lod=0&mesh").IndexBuffer).Count;
        var afterMesh = after.Mesh("?lod=0&mesh");
        var wheelBox = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
        var wheelAfter = sc.Groups(PreviewScene.Buffers.Of(after), afterMesh.Subs.Where(s => s.IOff >= origIb).Select(s => (afterMesh, s)),
            s => sc.Material(lkAfter, bindAfter, s.Name), Bones(ntAfter, wAfter), wheelBox);
        var beforeSubs = WheelGrafter.Family.Select(p => target.TryMesh($"?lod=0&{p}&mesh")).Where(m => m is { Empty: false })
                                            .SelectMany(m => m!.Subs.Select(s => (m!, s)));
        var wheelBefore = sc.Groups(PreviewScene.Buffers.Of(target), beforeSubs,
            s => sc.Material(lkBefore, bindBefore, s.Name), Bones(ntBefore, PreviewScene.NodeWorld(ntBefore)), new[] { 9f, 9f, 9f, -9f, -9f, -9f });

        List<Dictionary<string, object>>? body = null;
        var box = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
        if (withCar)
        {
            body = sc.Groups(PreviewScene.Buffers.Of(after), afterMesh.Subs.Where(s => s.IOff < origIb).Select(s => (afterMesh, s)),
                s => sc.Material(lkAfter, bindAfter, s.Name), Bones(ntAfter, wAfter), box);
            body.AddRange(sc.Tyres(target.Season, ntAfter, wAfter, commonIx, box));
        }

        var targetName = be.Entry.Kind == EntryKind.TeamChassis ? GameData.TeamName(be.Entry.SlotToken ?? be.ModelToken) : "FOM chassis";
        return new()
        {
            ["title"] = be.Entry.Title,
            ["afterName"] = GameData.DonorName(be.DonorToken) + " wheel",
            ["beforeName"] = targetName + " wheel (before)",
            ["toWheel"] = Viewer.Mat(toWheel),
            ["box"] = withCar ? box : wheelBox,
            ["wbox"] = wheelBox,
            ["body"] = body,
            ["after"] = wheelAfter,
            ["before"] = wheelBefore,
        };
    }

    static string FileLabel(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var paint = System.Text.RegularExpressions.Regex.Match(stem, @"paint-([a-z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return paint.Success ? paint.Groups[1].Value : System.Text.RegularExpressions.Regex.Replace(stem, @"^\d+--\d+--", "");
    }
}
