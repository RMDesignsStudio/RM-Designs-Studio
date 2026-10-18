using System.Numerics;
using EgoEngineLibrary.Archive.Erp;

namespace Swapper.Core;

/// <summary>
/// A suit set on a standing figure for the Studio viewer: the game's driver body in its standing (bind) pose wearing the
/// set's suit, with its attachments, boots, gloves, the logos and badges (their own decal geometry) and the glove logos,
/// each drawn with the materials the set binds to it. The in-car parts (in-car suit, harness, HANS device) are left out.
/// No head: the suit is what is shown.
/// </summary>
public static class SuitView
{
    /// <summary>How much closer the car views come for a figure (a car is about 5.6 m long, a driver about 1.8 m tall).</summary>
    public const float Closer = 0.5f;

    // what is drawn of each model (null = everything it has); in-car parts are left out, and so are the bare arms: the
    // gloves and sleeves cover them, and in the standing pose the hands' skin pokes through the thin gloves (skin-coloured spots)
    static readonly HashSet<string> BodySlots = ["livery_v2"];

    /// <summary>The figure's parts (viewer groups) for a suit set.</summary>
    public static List<Dictionary<string, object>> Groups(PreviewScene sc, GameData game, SuitSet set, float[] box, string gender = "male")
    {
        if (set.ErpPath is null) throw new InvalidDataException($"{set.Name} has no ERP");
        var erp = ErpX.Load(set.ErpPath);
        var layers = new List<IReadOnlyDictionary<string, ErpResource>> { ErpX.Index(erp) };
        // shared character resources (generic textures and materials) the set's materials may point at
        var common = Path.Combine(game.Assets, "character_package", "drivers", $"common_{gender}.erp");
        if (File.Exists(common)) layers.Add(ErpX.Index(ErpX.Load(common)));
        var lk = new PreviewScene.Lookup(layers);
        var buf = PreviewScene.Buffers.Of(lk);
        var root = $"eaid://character_package/team_livery/{set.Name}/idf/";
        var groups = new List<Dictionary<string, object>>();
        foreach (var (model, slots) in new (string Model, HashSet<string>? Slots)[]
                 {
                     ($"eaid://character_package/condition_scene/idf/driver_body_v2_{gender}", BodySlots),
                     ($"eaid://character_package/condition_scene/idf/driver_gloves", ["gloves"]),
                     (root + $"{set.Name}_{gender}", null),               // boots, suit attachments
                     (root + $"{set.Name}_body_logos_{gender}", null),    // logos and badges
                     (root + $"{set.Name}_glove_logos_{gender}", null),
                 })
        {
            if (lk.Find(model + ".idf?lod=0&mesh") is not { } meshRes) continue;
            var mesh = MeshDesc.Parse(ErpX.Data(meshRes));
            // the set's own binding for a shared model, else the model's default
            var bind = PreviewScene.Binding(lk, [$"{model}.emb?context={set.Name}", $"{model}.emb?context=default"]);
            var subs = mesh.Subs.Where(s => (slots is null || slots.Contains(s.Name)) && !s.Name.StartsWith("incar_"))
                                .GroupBy(s => (s.Name, s.ICount)).Select(g => g.First());   // a part listed twice (an equal copy, a body variant): the first; a smaller one of the same name is a part of its own (2024 Haas's attachment logo)
            groups.AddRange(sc.Groups(buf, subs.Select(s => (mesh, s)), s => sc.Material(lk, bind, s.Name), _ => Matrix4x4.Identity, box));
        }
        if (groups.Count == 0) throw new InvalidDataException($"{set.Name}: no suit model found");
        return groups;
    }

    /// <summary>A viewer "car" showing a suit set; with <paramref name="before"/>, the Before / After toggle compares it with
    /// that set (the template's own suits).</summary>
    public static Dictionary<string, object?> Car(PreviewScene sc, GameData game, SuitSet after, SuitSet? before, string title)
    {
        var box = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
        var a = Groups(sc, game, after, box);
        var b = before is null ? a : Groups(sc, game, before, box);
        return new()
        {
            ["title"] = title,
            ["afterName"] = after.Label + " " + after.Year + " suits",
            ["beforeName"] = (before ?? after).Label + " " + (before ?? after).Year + " suits (before)",
            ["toWheel"] = Viewer.Mat(Matrix4x4.Identity),
            ["box"] = box,
            ["wbox"] = box,
            ["body"] = new List<Dictionary<string, object>>(),
            ["after"] = a,
            ["before"] = b,
            ["person"] = Closer,
        };
    }
}
