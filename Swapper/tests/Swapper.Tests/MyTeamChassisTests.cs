using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// A real car as the My Team car (proved in game with the Red Bull, 2026-10-17/18): swapped into the FOM car's slot, the
/// six engine slots showing it, its binding answering every My Team livery, number and sticker name.
/// </summary>
public class MyTeamChassisTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;
    static ChassisDonor Car(string id) => Catalog.Chassis(Env.Game()).First(c => c.Id == id);

    [Fact]
    public void AMyTeamTemplateGetsANewChassisWithAWheel()
    {
        var tpl = new SerpsTemplate(Env.Template("2026 My Team - SERPs Template v1.1.serps"));
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var team = TemplateTeams.Of(tpl);
        var path = Path.Combine(Out, $"my team - red bull chassis {Guid.NewGuid():N}.serps");
        var outcome = SwapBuild.Run(Env.Game(), tpl, null, team, new(Car("redbull"), "ferrari", null, null), path, "test", new SwapLog());
        var mt = outcome.MyTeamChassis!;
        Assert.Equal(6, mt.Slots.Count);
        Assert.Equal(100, mt.ContextsOwn);                       // its numbers 1-99 and default
        Assert.True(mt.ContextsNumber > 4000);
        Assert.Equal("driver_in_car_fom_26", outcome.Chassis!.TeamAnimset);
        Assert.Contains("/rmk", mt.Model);                        // the wheel build's private model, which every slot shows
        var back = new SerpsTemplate(path);
        var part = back.EntryNames.Single(n => n.Contains("Livery - Red Bull Chassis - 26 Ferrari Wheel/") && n.EndsWith(".erp"));
        var erp = back.Get(part);
        Assert.True(MyTeamChassis.Verify(Env.Game(), erp, mt, 2026) > 0);
        var ids = ErpX.Load(erp).Resources.Select(r => r.Identifier).ToHashSet();
        Assert.Contains("eaid://f1_2026_vehicle_package/teams/fom_car_fom_car/handling/fom_car_fom_car.vtf", ids);
        Assert.All(GameData.MyTeamSlots.Where(s => s != "fom_car"), s => Assert.Contains($"eaid://f1_2026_vehicle_package/teams/{s}/handling/{s}.vtf", ids));
    }

    /// <summary>Every 2026 car (special liveries too) has its own number bindings 1-99, which the My Team numbers use.</summary>
    [Fact]
    public void EveryCarHasItsOwnNumbers()
    {
        var game = Env.Game();
        foreach (var car in Catalog.Chassis(game).Where(c => c.Season == 2026))
        {
            var ids = ErpX.Load(car.ErpPath).Resources.Select(r => r.Identifier).ToHashSet();
            var binding = ids.Where(i => i.EndsWith(".emb?context=default", StringComparison.Ordinal) && i.Contains("/teams/")).ToList();
            Assert.True(binding.Count > 0, $"{car.Id}: no default binding");
            var b = binding[0][..^"default".Length];
            var missing = Enumerable.Range(1, 99).Count(n => !ids.Contains(b + n));
            Assert.True(missing == 0, $"{car.Id}: {missing} numbers missing on {b}");
        }
    }
}
