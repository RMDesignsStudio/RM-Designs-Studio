using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// Engine sounds: every power unit of the 2026 and 2025 cars, named by maker with the car it comes from, onto a template's
/// options or a new chassis; only the car-model layer's switch changes.
/// </summary>
public class EngineTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;

    static EngineSound Engine(string id) => EngineSwap.Catalog(Env.Game()).First(e => e.Id == id);

    [Fact]
    public void TheGameOffersEachTeamsEngineByMaker()
    {
        var all = EngineSwap.Catalog(Env.Game());
        Assert.Equal(all.Count, all.Select(e => e.Switch).Distinct().Count());
        Assert.Equal("Ferrari", Engine("haas").Maker);
        Assert.Equal("Haas", Engine("haas").Source);
        Assert.Equal("RBPT Ford", Engine("redbull").Maker);
        Assert.Equal("Honda RBPT", Engine("toro_rosso@2025").Maker);
        Assert.Equal("Honda", Engine("force_india").Maker);
        Assert.Equal("Mercedes", Engine("force_india@2025").Maker);
        Assert.Equal("Renault", Engine("lotus@2025").Maker);
        Assert.Equal("Audi", Engine("sauber").Maker);
        Assert.Equal("My Team", Engine("myteam_honda").Source);
        Assert.Equal("Ferrari Engine (Kick Sauber 25)", EngineSwap.Suffix(Engine("sauber@2025")));
    }

    [Fact]
    public void AnOptionGetsASectionWithTheEngineAndOnlyItsSwitchChanges()
    {
        var src = Env.Template("2026 Cadillac - SERPs Template v1.1.serps");
        var tpl = new SerpsTemplate(src);
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var entry = tpl.CarEntries.First(c => c.Kind == EntryKind.TeamChassis);
        var path = Path.Combine(Out, "cadillac - red bull 25 engine.serps");
        SwapBuild.Run(Env.Game(), tpl, null, TemplateTeams.Of(tpl), new(null, null, [entry], null, null, Engine("redbull@2025")), path, "test", new SwapLog());
        var back = new SerpsTemplate(path);
        const string section = "ERPs/02--01--Livery - Default Chassis - Honda RBPT Engine (Red Bull 25)/";
        var part = section + entry.ZipPath[(entry.Folder.Length + 1)..];
        Assert.Contains(part, back.EntryNames);
        // the car-model layer: only the switch line differs from the template's
        string Layer(byte[] erp) => AnimsetSwap.ToJson(ErpX.Data(ErpX.Load(erp).Resources.First(r => r.Identifier.EndsWith("-carmodel.layer"))));
        var before = Layer(tpl.Get(entry.ZipPath)).Split('\n');
        var after = Layer(back.Get(part)).Split('\n');
        Assert.Equal(before.Length, after.Length);
        var diff = before.Zip(after).Where(p => p.First != p.Second).ToList();
        Assert.Single(diff);
        Assert.Contains("f1_m_red_bull", diff[0].Second);
        Assert.Equal("f1_m_red_bull", EngineSwap.SwitchOf(ErpX.Load(back.Get(part))));
        // and the ending is read back: the section given another engine is renamed where it is
        Assert.Equal<(string, string?, string?, string?)>(("Livery - Default Chassis", null, null, "Honda RBPT Engine (Red Bull 25)"),
                     Sections.Endings("Livery - Default Chassis - Honda RBPT Engine (Red Bull 25)"));
    }

    [Fact]
    public void ANewChassisCanHaveAnotherEngine()
    {
        var src = Env.Template("2026 Williams - SERPs Template v1.1.serps");
        var tpl = new SerpsTemplate(src);
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var path = Path.Combine(Out, "williams - ferrari chassis honda engine.serps");
        var car = Catalog.Chassis(Env.Game()).First(c => c.Id == "ferrari");
        SwapBuild.Run(Env.Game(), tpl, null, TemplateTeams.Of(tpl), new(car, null, null, null, null, Engine("myteam_honda")), path, "test", new SwapLog());
        var back = new SerpsTemplate(path);
        var part = back.EntryNames.Single(n => n.Contains("Livery - Ferrari Chassis - Honda Engine (My Team 26)/") && n.EndsWith(".erp"));
        Assert.Equal("f1_m_myteam_honda_26", EngineSwap.SwitchOf(ErpX.Load(back.Get(part))));
    }
}
