using Swapper.Core;
using Swapper.Core.TeamSimplified;

namespace Swapper.Tests;

public class CatalogTests
{
    [Fact]
    public void SuitSetNamesAreRead()
    {
        var s = Catalog.ParseSuitSet("2025-mclarensilverstone")!;
        Assert.Equal((2025, "F1", "mclaren", "McLaren", "Silverstone"), (s.Year, s.Series, s.Key, s.Team, s.Special));
        var f2 = Catalog.ParseSuitSet("2024-f2-premaracing")!;
        Assert.Equal(("F2", "Prema", "prema"), (f2.Series, f2.Team, f2.ColorKey));
        Assert.Equal("Audi", Catalog.ParseSuitSet("2026-sauber")!.Team);
        Assert.Equal("Kick Sauber", Catalog.ParseSuitSet("2025-sauber")!.Team);
        Assert.Equal("kicksauber", Catalog.ParseSuitSet("2025-saubermiami")!.ColorKey);
        Assert.Null(Catalog.ParseSuitSet("common"));
    }

    [Fact]
    public void LiveryNamesComeFromTheFile()
    {
        Assert.Equal("Suzuka 26", CarErps.LiveryName(@"x\teams\toro_rosso\wep\toro_rosso_suzuka_26.erp", "toro_rosso"));
        Assert.Null(CarErps.LiveryName(@"x\teams\toro_rosso\wep\toro_rosso.erp", "toro_rosso"));
        Assert.Equal("toro_rosso", CarErps.TeamOf(@"x\teams\toro_rosso\wep\toro_rosso_suzuka_26.erp"));
    }

    [Fact]
    public void TheGameOffersCarsWheelsAndSuits()
    {
        var game = Env.Game();
        var cars = Catalog.Chassis(game);
        // every 2026 and 2025 team's own car (not the FOM car), each first in its team, and special liveries that are whole cars
        Assert.Equal(11, cars.Count(c => c.Livery is null && c.Season == 2026));
        Assert.Equal(10, cars.Count(c => c.Livery is null && c.Season == 2025));
        Assert.Contains(cars, c => c.Id == "williams_lasvegas_25@2025" && c.ColorKey == "williams@2025");
        // the 2025 liveries the 2026 package carries without their full textures are left out; the 2025 cars are there
        Assert.DoesNotContain(cars, c => c.Id == "toro_rosso_silverstone_25");
        Assert.Contains(cars, c => c.Id == "toro_rosso_silverstone_25@2025");
        Assert.DoesNotContain(cars, c => c.Team == "fom_car");
        Assert.Contains(cars, c => c.Id == "toro_rosso_suzuka_26");
        // texture-only liveries (no /wep/ setup) are not cars
        Assert.DoesNotContain(cars, c => c.Id == "toro_rosso_barcelona_26");
        // one car under two names (force_india_monaco_2026 loads force_india_monaco_26's model): listed once
        Assert.Contains(cars, c => c.Id == "force_india_monaco_26");
        Assert.DoesNotContain(cars, c => c.Id == "force_india_monaco_2026");
        var suits = Catalog.Suits(game);
        Assert.Contains(suits, s => s.Name == "2026-ferrari" && s.ErpPath is not null);
        Assert.Contains(suits, s => s.Series == "F2" && s.Year == 2024);
        Assert.Equal(23, Catalog.Wheels.Count);   // the 2026 and 2025 cars, each season's FOM chassis included
    }

    [Fact]
    public void TemplatesSayWhoseTheyAre()
    {
        var williams = new SerpsTemplate(Env.Template("2026 Williams - SERPs Template v1.1.serps"));
        var team = TemplateTeams.Of(williams)!;
        Assert.Equal(("Williams", "2026-williams", true), (team.Name, team.SuitSet, team.ChassisSwapWorks));
        var myTeam = new SerpsTemplate(Env.Template("2026 My Team - SERPs Template v1.1.serps"));
        Assert.True(TemplateTeams.Of(myTeam)!.IsMyTeam);
        Assert.False(team.IsMyTeam);
        Assert.Null(TemplateTeams.ShaderPackage("not json"));
    }
}
