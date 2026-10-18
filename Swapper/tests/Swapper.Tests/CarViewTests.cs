using Swapper.Core;

namespace Swapper.Tests;

public class CarViewTests
{
    static int Groups(Dictionary<string, object?> car, string key) => ((System.Collections.ICollection)car[key]!).Count;

    [Fact]
    public void ATemplateCarHasItsBodyAndItsWheel()
    {
        var game = Env.Game();
        var tpl = new SerpsTemplate(Env.Template("2026 Ferrari - SERPs Template v1.1.serps"));
        var sc = new PreviewScene(game) { TexSize = 64, PaintSize = 64 };
        var car = CarView.Template(sc, game, tpl, tpl.CarEntries[0]);
        Assert.True(Groups(car, "body") > 20);
        Assert.True(Groups(car, "after") > 0);
        Assert.Same(car["after"], car["before"]);
    }

    [Fact]
    public void AGameCarCanBeShownStraightFromTheGame()
    {
        var game = Env.Game();
        var sc = new PreviewScene(game) { TexSize = 64, PaintSize = 64 };
        var suzuka = Catalog.Chassis(game).First(c => c.Id == "toro_rosso_suzuka_26");
        var car = CarView.Stock(sc, game, suzuka);
        Assert.True(Groups(car, "body") > 20);
        Assert.True(Groups(car, "after") > 0);
    }

    [Fact]
    public void ASuitSetStandsWithItsLogos()
    {
        var game = Env.Game();
        var sc = new PreviewScene(game) { TexSize = 64, PaintSize = 64 };
        var suits = Catalog.Suits(game);
        var car = SuitView.Car(sc, game, suits.First(s => s.Name == "2026-mclaren"), suits.First(s => s.Name == "2026-ferrari"), "test");
        var after = (List<Dictionary<string, object>>)car["after"]!;
        // suit, arms, gloves, boots, attachments, the logos (decals: kind 6) and glove logos
        Assert.True(after.Count >= 7);
        Assert.Contains(after, g => (string)g["slot"] == "livery_v2");
        Assert.Contains(after, g => (string)g["slot"] == "livery_v2_logo" && (int)sc.Materials[(int)g["m"]]["k"]! == PreviewScene.KDecal);
        Assert.DoesNotContain(after, g => ((string)g["slot"]).StartsWith("incar_") || (string)g["slot"] is "harness" or "hans_device");
        Assert.NotSame(car["after"], car["before"]);
        // standing: about 1.8 m tall, feet on the floor
        var box = (float[])car["box"]!;
        Assert.InRange(box[1], -0.05f, 0.05f);
        Assert.InRange(box[4], 1.5f, 2.0f);
    }

    [Fact]
    public void AMultiPaintLiveryKeepsItsLayers()
    {
        // Aston Martin's Monaco 26 car has no colour map: four colour layers placed by a mask, mixed by the page (kind 10)
        var game = Env.Game();
        var sc = new PreviewScene(game) { TexSize = 64, PaintSize = 64 };
        CarView.Stock(sc, game, Catalog.Chassis(game).First(c => c.Id == "force_india_monaco_26"));
        var paint = sc.Materials.First(m => ((string)m["name"]!).EndsWith("monaco_26_paint.material"));
        Assert.Equal(PreviewScene.KMultiPaint, paint["k"]);
        var t = (Dictionary<string, int>)paint["t"]!;
        Assert.True(t["d"] >= 0);
        // 4 layers x 4 values: layer 1 is the colour-shift one (maroon, its metallic reflections orange-gold)
        var ml = (float[])paint["ml"]!;
        Assert.Equal(64, ml.Length);
        Assert.Equal(1f, ml[8]);              // layer 1's metallic flip colour, red
        Assert.Equal(1f, ml[13]);             // ... is used
        sc.Finish();
        Assert.Equal("rgba", sc.Textures[t["d"]]["f"]);   // the mask, passed as it is (linear)
    }
}
