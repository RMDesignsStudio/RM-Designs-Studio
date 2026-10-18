using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

// swapper inspect TEMPLATE                     the template's team and chassis options
// swapper cars|suits|wheels [--game DIR]       what the game offers for each pick
// swapper build TEMPLATE [--car ID] [--wheel ID] [--rims ID] [--engine ID] [--entries 0,1] [--suits SET] --out FILE.serps
//     a new template or project: a game car as a new chassis option (the wheel and rims built onto it when given, else
//     onto the template's options, all or --entries), and / or another suit set
// swapper preview TEMPLATE [--entry N] [--chassis STEM [--no-template]] [--wheel ID] [--rims ID] [--mode car|wheel] --out FILE.html [--game DIR]
//     a 3D page: the template's chassis option N (default 0) as it is, or with a wheel built in (in memory, nothing
//     saved), or a game car (--chassis) beside it

static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

if (args.Length == 0) { Console.WriteLine("swapper inspect|cars|suits|wheels|suit|build|preview ..."); return 1; }
var gameDir = Opt(args, "--game") ?? Swapper.Core.TeamSimplified.GameDirDetector.Detect()?.Path ?? GameData.DetectGameDir() ?? throw new InvalidOperationException("F1 25 not found: pass --game DIR");
var game = new GameData(gameDir);

switch (args[0])
{
    case "inspect":
    {
        var tpl = new SerpsTemplate(args[1]);
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var team = TemplateTeams.Of(tpl);
        Console.WriteLine($"{tpl.Name}: {team?.Name ?? "team not recognised"} (shader package {TemplateTeams.ShaderPackage(tpl.Comment) ?? "-"}, suits {team?.SuitSet ?? "-"})");
        for (int i = 0; i < tpl.CarEntries.Count; i++) Console.WriteLine($"  [{i}] {tpl.CarEntries[i].Describe}{(tpl.CarEntries[i].Problem is { } p ? "  ! " + p : "")}");
        return 0;
    }
    case "cars":
        foreach (var c in Catalog.Chassis(game)) Console.WriteLine($"{c.Id,-28} {c.Label}");
        return 0;
    case "suits":
        foreach (var s in Catalog.Suits(game)) Console.WriteLine($"{s.Name,-28} {s.Year} {s.Series} {s.Label}  [{s.ColorKey}]");
        return 0;
    case "wheels":
        foreach (var w in Catalog.Wheels) Console.WriteLine($"{w.Id,-20} {w.Label}");
        return 0;
    case "suit":
    {
        // swapper suit SET [--before SET] --out FILE.html: a suit set on a standing figure
        var suits = Catalog.Suits(game);
        var after = suits.First(x => x.Name == args[1]);
        var before = Opt(args, "--before") is { } bn ? suits.First(x => x.Name == bn) : null;
        var outPath = Opt(args, "--out") ?? throw new InvalidOperationException("--out FILE.html");
        var sc = new PreviewScene(game) { TexSize = 1024, PaintSize = 2048 };
        var car = SuitView.Car(sc, game, after, before, after.Label);
        File.WriteAllText(outPath, $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Swapper suit</title><style>{{Viewer.Css}}</style></head>
            <body class="page"><main>{{Viewer.Markup("pv", [car], new Viewer.Options(Sides: before is not null, Modes: false, Mode: "car"), sc)}}</main></body></html>
            """, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"{outPath}: {((System.Collections.ICollection)car["after"]!).Count} parts");
        return 0;
    }
    case "engines":
    {
        // swapper engines: every engine sound (id, switch, name)
        foreach (var e in EngineSwap.Catalog(game)) Console.WriteLine($"{e.Id,-22} {e.Switch,-26} {e.Maker,-12} {e.Source} {e.Season}");
        return 0;
    }
    case "build":
    {
        // swapper build TEMPLATE [--car ID] [--wheel ID] [--rims ID] [--engine ID] [--entries 0,1] [--suits SET] --out FILE.serps (Swapper writes no .simplified projects)
        var tpl = new SerpsTemplate(args[1]);
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var project = SimplifiedProject.IsProject(args[1]) ? SimplifiedProject.Read(args[1]) : null;
        var team = project is not null ? TemplateTeams.Of(project) : TemplateTeams.Of(tpl);
        var car = Opt(args, "--car") is { } id ? Catalog.Chassis(game).FirstOrDefault(c => c.Id == id) ?? throw new InvalidOperationException($"no car {id} (see: swapper cars)") : null;
        // in-game diagnosis of the rims: textures | nodiscs | lod0 (comma separated)
        foreach (var t in (Opt(args, "--rims-test") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            switch (t) { case "textures": RimSwap.Test.TexturesOnly = true; break; case "nodiscs": RimSwap.Test.NoDiscs = true; break; case "lod0": RimSwap.Test.MaxLod = 0; break; }
        var rims = Opt(args, "--rims") is { } rid ? Catalog.Chassis(game).FirstOrDefault(c => c.Id == rid) ?? throw new InvalidOperationException($"no car {rid} (see: swapper cars)") : null;
        var engine = Opt(args, "--engine") is { } eid ? EngineSwap.Catalog(game).FirstOrDefault(e => e.Id == eid) ?? throw new InvalidOperationException($"no engine sound {eid} (see: swapper engines)") : null;
        var suits = Opt(args, "--suits") is { } sn ? Catalog.Suits(game).FirstOrDefault(s => s.Name == sn) ?? throw new InvalidOperationException($"no suit set {sn} (see: swapper suits)") : null;
        var entries = Opt(args, "--entries") is { } en ? en.Split(',').Select(i => tpl.CarEntries[int.Parse(i)]).ToList()
                    : tpl.CarEntries.Where(e => e.Problem is null).ToList();
        var outPath = Opt(args, "--out") ?? throw new InvalidOperationException("--out FILE");
        var log = new SwapLog();
        log.Line += (line, warn) => Console.WriteLine((warn ? "warning: " : "  ") + line);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var checks = SwapBuild.Run(game, tpl, project, team, new(car, Opt(args, "--wheel"), entries, suits, rims, engine), outPath, "cli", log).Checks;
        Console.WriteLine($"{outPath}: {checks} checks passed ({sw.Elapsed.TotalSeconds:0.0} s)");
        return 0;
    }
    case "preview":
    {
        var tpl = new SerpsTemplate(args[1]);
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var entry = tpl.CarEntries[int.Parse(Opt(args, "--entry") ?? "0")];
        var outPath = Opt(args, "--out") ?? throw new InvalidOperationException("--out FILE.html");
        var sc = new PreviewScene(game) { TexSize = 1024, PaintSize = 2048 };
        var cars = new List<Dictionary<string, object?>>();
        if (args.Contains("--no-template")) { }
        else if (Opt(args, "--rims") is { } rimsId)   // the option with new rims (and the wheel when given), as the app shows it
            cars.Add(ChassisView.WithRims(sc, game, tpl, entry, null, Opt(args, "--wheel"),
                Catalog.Chassis(game).First(c => c.Id == rimsId), entry.Title + " + " + rimsId + " rims"));
        else if (Opt(args, "--wheel") is { } wheel)
        {
            var log = new SwapLog();
            var res = tpl.BuildInMemory(game, wheel, [entry], log, carPlacement: true, anySeason: true);   // as the build places it
            cars.Add(WheelPreview.Car(sc, game, tpl, res, res.Entries[0], withCar: true));
        }
        else cars.Add(CarView.Template(sc, game, tpl, entry));
        // a game car (its id, "redbull@2025" for a 2025 one), with the wheel built in when --wheel is given
        if (Opt(args, "--chassis") is { } id)
            cars.Add(ChassisView.Car(sc, game, Catalog.Chassis(game).First(c => c.Id == id), Opt(args, "--wheel")));
        File.WriteAllText(outPath, $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Swapper preview</title><style>{{Viewer.Css}}</style></head>
            <body class="page"><main>{{Viewer.Markup("pv", cars, new Viewer.Options(Mode: Opt(args, "--mode") ?? "car"), sc)}}</main></body></html>
            """, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"{outPath}: {cars.Count} car(s)");
        return 0;
    }
}
Console.WriteLine("unknown command " + args[0]);
return 1;
