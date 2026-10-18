using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// Rims (the cars' road wheels): any car's rims of the same season onto a template's chassis option or a new chassis, as a
/// section of its own, alone or with a new wheel; every donor fits; the car model is rebuilt consistently.
/// </summary>
public class RimTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;
    const string Ferrari = "2026 Ferrari - SERPs Template v1.1.serps";
    const string Default = "ERPs/02--01--Livery - Default Chassis/";

    static ChassisDonor Car(string id) => Catalog.Chassis(Env.Game()).First(c => c.Id == id);

    static SerpsTemplate Build(string source, SwapBuild.Picks picks, string outPath)
    {
        var tpl = new SerpsTemplate(source);
        WheelBuild.MarkUnsupported(tpl);
        SwapBuild.Run(Env.Game(), tpl, null, TemplateTeams.Of(tpl), picks, outPath, "test", new SwapLog());
        var back = new SerpsTemplate(outPath);
        WheelBuild.MarkUnsupported(back);
        return back;
    }

    [Fact]
    public void EveryCarsRimsFitTheFerrari()
    {
        var tpl = new SerpsTemplate(Env.Template(Ferrari));
        var entry = tpl.CarEntries.First(c => c.Folder + "/" == Default);
        List<EgoEngineLibrary.Archive.Erp.ErpFile> Others() => tpl.EntryNames.Where(n => n.EndsWith(".erp") && n != entry.ZipPath && !n.Contains("FOM")).Select(n => ErpX.Load(tpl.Get(n))).ToList();
        foreach (var donor in Catalog.Chassis(Env.Game()).Where(c => c.Season == 2026))
        {
            var (erp, r) = RimSwap.Run(Env.Game(), tpl.Get(entry.ZipPath), 2026, donor, Others(), new SwapLog());
            Assert.True(RimSwap.Verify(Env.Game(), erp, 2026, r, Others()) > 40, donor.Id);
            Assert.Equal([0, 1, 2, 3, 4], r.Lods);                 // the far levels draw the wheels with the body livery: kept
            Assert.True(r.PrivateCopy, donor.Id);                  // the game's model is never changed
            Assert.Equal(3, r.Textures.Count);
        }
    }

    [Fact]
    public void ATemplateOptionGetsASectionWithTheRims()
    {
        var src = Env.Template(Ferrari);
        var tpl = new SerpsTemplate(src);
        var back = Build(src, new(null, null, [tpl.CarEntries.First(c => c.Folder + "/" == Default)], null, Car("williams")), Path.Combine(Out, "ferrari - williams rims.serps"));
        const string section = "ERPs/02--01--Livery - Default Chassis - Williams Rims/";
        Assert.Equal(tpl.EntryNames, back.EntryNames.Where(n => !n.StartsWith(section)));
        Assert.Contains(back.CarEntries, c => c.Folder + "/" == section && c.Problem is null);
        // the original option is kept byte for byte
        foreach (var n in tpl.EntryNames.Where(n => n.StartsWith(Default) && !n.EndsWith('/'))) Assert.Equal(tpl.Get(n), back.Get(n));
    }

    [Fact]
    public void AWheelAndRimsGoTogetherAndASectionThisAppMadeTakesMore()
    {
        var src = Env.Template(Ferrari);
        var tpl = new SerpsTemplate(src);
        var entry = tpl.CarEntries.First(c => c.Folder + "/" == Default);
        var both = Build(src, new(null, "mclaren", [entry], null, Car("haas")), Path.Combine(Out, "ferrari - mclaren wheel haas rims.serps"));
        const string section = "ERPs/02--01--Livery - Default Chassis - 26 McLaren Wheel - Haas Rims/";
        var ce = Assert.Single(both.CarEntries, c => c.Folder + "/" == section);
        Assert.NotNull(ce.PreviousSwap);   // the wheel engine still knows its wheel is in there

        // a section this app made, given other rims: changed where it is, keeping its wheel
        var again = Build(both.SourcePath, new(null, null, [ce], null, Car("redbull")), Path.Combine(Out, "ferrari - again red bull rims.serps"));
        Assert.Contains(again.EntryNames, n => n == "ERPs/02--01--Livery - Default Chassis - 26 McLaren Wheel - Red Bull Rims/");
        Assert.DoesNotContain(again.EntryNames, n => n.Contains("Haas Rims"));
    }

    [Fact]
    public void FomChassisOptionsTakeRims()
    {
        // a team template's FOM option (the slot pointed at the game's fom_car model) and a My Team livery (a binding only,
        // worn by the FOM car of the fom_car / myteam_<engine> slots, whose car-model layers come along)
        var cad = new SerpsTemplate(Env.Template("2026 Cadillac - SERPs Template v1.1.serps"));
        var fom = Assert.Single(cad.CarEntries, c => c.Kind == EntryKind.FomChassis);
        var back = Build(cad.SourcePath, new(null, "ferrari", [fom], null, Car("sauber")), Path.Combine(Out, "cadillac - fom ferrari wheel audi rims.serps"));
        Assert.Contains(back.CarEntries, c => c.Folder == "ERPs/03--01--Livery - FOM Chassis - 26 Ferrari Wheel - Audi Rims" && c.Problem is null);

        var mt = new SerpsTemplate(Env.Template("2026 My Team - SERPs Template v1.1.serps"));
        var livery = mt.CarEntries.First(c => c.Kind == EntryKind.MyTeamLivery);
        var (erp, r) = RimSwap.Run(Env.Game(), mt.Get(livery.ZipPath), 2026, Car("mclaren"), [], new SwapLog(), myTeam: true);
        Assert.True(RimSwap.Verify(Env.Game(), erp, 2026, r, []) > 40);
        Assert.True(r.PrivateCopy);
        Assert.StartsWith("fom", r.ModelToken);
        var layers = ErpX.Load(erp).Resources.Where(x => x.Identifier.EndsWith("-carmodel.layer")).Select(x => x.Identifier.Split('/')[^1]).ToList();
        Assert.Contains("fom_car-carmodel.layer", layers);
        Assert.All(r.Textures, t => Assert.Contains("/myteam_", t));   // the livery's own rim textures
    }

    [Fact]
    public void ANewChassisCanHaveNewRims()
    {
        var back = Build(Env.Template("2026 Racing Bulls - SERPs Template v1.1.serps"), new(Car("redbull"), null, null, null, Car("ferrari")),
                         Path.Combine(Out, "racing bulls - rb22 ferrari rims.serps"));
        Assert.Contains(back.CarEntries, c => c.Folder == "ERPs/03--01--Livery - Red Bull Chassis - Ferrari Rims" && c.Problem is null);
    }

    [Fact]
    public void RimsOfAnotherSeasonAreRefused()
    {
        var tpl = new SerpsTemplate(Env.Template(Ferrari));
        Assert.NotNull(SwapBuild.RimsProblem(tpl, null, Car("williams@2025")));
        Assert.Null(SwapBuild.RimsProblem(tpl, null, Car("toro_rosso_suzuka_26")));
        Assert.Equal<(string, string?, string?, string?)>(("Default Chassis", "26 McLaren Wheel", "Haas Rims", null), Sections.Endings("Default Chassis - 26 McLaren Wheel - Haas Rims"));
        Assert.Equal<(string, string?, string?, string?)>(("Default Chassis", null, "Racing Bulls Suzuka 26 Rims", null), Sections.Endings("Default Chassis - Racing Bulls Suzuka 26 Rims"));
    }
}
