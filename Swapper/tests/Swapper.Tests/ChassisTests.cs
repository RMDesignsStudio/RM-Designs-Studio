using System.IO.Compression;
using System.Text.Json.Nodes;
using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// The chassis swap (Team Simplified's, ported): the car it makes matches the swaps the user made with their app resource
/// for resource, and it goes into a new template or project as a chassis option of its own, beside the template's.
/// </summary>
public class ChassisTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;

    const string RacingBulls = "2026 Racing Bulls - SERPs Template v1.1.serps";
    const string RedBullOption = "ERPs/03--01--Livery - Red Bull Chassis/";
    const string RedBullPart = RedBullOption + "01--00--26-racingbulls_car_swapped_livery-and-chassis.erp";

    static ChassisDonor Car(string id) => Catalog.Chassis(Env.Game()).First(c => c.Id == id);

    static string[] Enabled(string path)
    {
        using var z = ZipFile.OpenRead(path);
        using var s = z.GetEntry("info.json")!.Open();
        return JsonNode.Parse(s)!["enabled"]!.AsArray().Select(x => x!.GetValue<string>()).Order().ToArray();
    }

    static string Build(string source, string carId, string outPath, string? wheel = null)
    {
        var tpl = new SerpsTemplate(source);
        WheelBuild.MarkUnsupported(tpl);
        var project = SimplifiedProject.IsProject(source) ? SimplifiedProject.Read(source) : null;
        SwapBuild.Run(Env.Game(), tpl, project, project is null ? TemplateTeams.Of(tpl) : TemplateTeams.Of(project), new(Car(carId), wheel, null, null), outPath, "test", new SwapLog());
        return outPath;
    }

    [Theory]
    [InlineData("2026 Racing Bulls - SERPs Template v1.1 - RB22 Chassis.serps", "redbull", "toro_rosso", "swapcar")]
    [InlineData("2026 Williams - SERPs Template v1.1 - MCL40 Silverstone Chassis.serps", "mclaren_silverstone_26", "williams", "williams_mclgbrchassis")]
    public void TheSwapMatchesTeamSimplifiedsAppResourceForResource(string golden, string carId, string slot, string newName)
    {
        // templates the user swapped with Team Simplified's Swapper (and raced): the same car, slot and name give the same
        // resources, in the same order, with the same payloads (only the resource hashes differ: ours are renewed)
        var path = Path.Combine(Env.TemplateDir, "Swapped Templates", golden);
        if (!File.Exists(path)) Assert.Skip($"not found: {path}");
        using var z = ZipFile.OpenRead(path);
        using var ms = new MemoryStream();
        using (var s = z.Entries.First(e => e.FullName.Contains("_car_swapped_")).Open()) s.CopyTo(ms);
        var theirs = ErpX.Load(ms.ToArray());
        var (bytes, result) = ChassisSwap.Run(Env.Game(), Car(carId), slot, newName, new SwapLog());
        var ours = ErpX.Load(bytes);
        Assert.Equal(theirs.Resources.Select(r => r.Identifier), ours.Resources.Select(r => r.Identifier));
        for (int i = 0; i < ours.Resources.Count; i++)
        {
            var a = ours.Resources[i]; var b = theirs.Resources[i];
            Assert.Equal(b.Fragments.Count, a.Fragments.Count);
            for (int f = 0; f < a.Fragments.Count; f++)
                Assert.True(a.Fragments[f].GetDataArray(true).AsSpan().SequenceEqual(b.Fragments[f].GetDataArray(true)), $"{a.Identifier} fragment {f} differs");
            Assert.True(ErpX.HashValid(a), $"{a.Identifier} has a stale hash");
        }
        Assert.True(result.AnimsetTransplanted);
        Assert.Empty(result.Warnings);
        Assert.True(ChassisSwap.Verify(Env.Game(), bytes, result, 2026) > 10);
    }

    [Theory]
    [InlineData("force_india", "gm_cadillac", "0.032")]   // Aston Martin into Cadillac: drawn 33 mm higher than Cadillac's own
    [InlineData("redbull", "toro_rosso", "0.037")]
    public void TheSwappedCarIsDrawnAtItsOwnHeight(string carId, string slot, string body)
    {
        // the team's handling places the drawn car for the team's model; the swap carries it with the donor's four drawing
        // values, and nothing else of it changes
        var (bytes, result) = ChassisSwap.Run(Env.Game(), Car(carId), slot, ChassisSwap.NewName(Car(carId), slot), new SwapLog());
        var (withLook, look) = CarLook.Apply(Env.Game(), bytes, Car(carId), slot, new SwapLog());
        Assert.Equal(2, look.Files.Count);   // the team's own handling and its equal-cars one
        Assert.Equal(body, look.Values["Chassis/FrontVisualHeightOffset"]);
        Assert.Equal(8, CarLook.Verify(Env.Game(), withLook, Car(carId), slot, look));
        Assert.True(ChassisSwap.Verify(Env.Game(), withLook, result, 2026) > 10);
        // a car drawn like the team's own needs nothing
        var (same, none) = CarLook.Apply(Env.Game(), bytes, Car("gm_cadillac_miami_26"), "gm_cadillac", new SwapLog());
        Assert.Empty(none.Files);
        Assert.Same(bytes, same);
    }

    [Fact]
    public void TheNewNameIsTheDonorsLengthAndItsOwn()
    {
        foreach (var car in Catalog.Chassis(Env.Game()))
            foreach (var slot in new[] { "toro_rosso", "williams", "haas" })
            {
                var name = ChassisSwap.NewName(car, slot);
                Assert.Equal(car.Stem.Length, name.Length);
                Assert.NotEqual(car.Stem, name);
                Assert.Matches("^rm[a-z0-9]+$", name);
                Assert.Equal(name, ChassisSwap.NewName(car, slot));
            }
    }

    [Fact]
    public void ATemplateGetsTheCarAsAChassisOptionOfItsOwn()
    {
        var src = Env.Template(RacingBulls);
        var tpl = new SerpsTemplate(src);
        var path = Build(src, "redbull", Path.Combine(Out, "racing bulls - rb22.serps"));
        var back = new SerpsTemplate(path);
        // the template's files are all there unchanged and in order (the save checks the bytes); the new option sits
        // after the FOM chassis, as Team Simplified's app places it
        Assert.Equal(tpl.EntryNames, back.EntryNames.Where(n => !n.StartsWith(RedBullOption)));
        var names = back.EntryNames.ToList();
        Assert.Equal(names.IndexOf("ERPs/03--01--Livery - FOM Chassis/") + 1, names.IndexOf(RedBullOption));
        Assert.Equal(names.IndexOf("ERPs/03--01--Livery - FOM Chassis/02--00--26-racingbulls_car_FOM_numbers.erp") + 1, names.IndexOf(RedBullPart));
        Assert.Equal(tpl.Comment, back.Comment);
        Assert.True(CarErps.IsCar(ErpX.Load(back.Get(RedBullPart))));

        // the same car again (into the new file) replaces its option rather than adding a second
        var again = Build(path, "redbull", Path.Combine(Out, "racing bulls - rb22 again.serps"));
        Assert.Single(new SerpsTemplate(again).EntryNames, n => n == RedBullOption);
        // another car is one more option
        var two = Build(path, "mclaren", Path.Combine(Out, "racing bulls - two.serps"));
        var twoNames = new SerpsTemplate(two).EntryNames;
        Assert.Contains(RedBullOption, twoNames);
        Assert.Contains("ERPs/03--01--Livery - McLaren Chassis/", twoNames);
    }

    [Fact]
    public void ANewChassisCanHaveANewWheel()
    {
        // chassis then wheel, as the user's "RB22 Chassis - McLaren wheel" template was made (and raced): one option holding
        // the swapped car with the wheel built in; its car-model layers point at the wheel build's private model
        var path = Build(Env.Template(RacingBulls), "redbull", Path.Combine(Out, "racing bulls - rb22 + mclaren wheel.serps"), "mclaren");
        const string option = "ERPs/03--01--Livery - Red Bull Chassis - 26 McLaren Wheel/";
        var back = new SerpsTemplate(path);
        Assert.Contains(option, back.EntryNames);
        Assert.DoesNotContain(RedBullOption, back.EntryNames);
        var erp = ErpX.Load(back.Get(option + "01--00--26-racingbulls_car_swapped_livery-and-chassis.erp"));
        var layer = System.Text.Encoding.ASCII.GetString(ErpX.Data(erp.Resources.First(r => r.Identifier.EndsWith("/toro_rosso-carmodel.layer"))));
        var model = System.Text.RegularExpressions.Regex.Match(layer, @"/idf/([a-z0-9_]+)\.idf\?context=render").Groups[1].Value;
        Assert.NotEqual(ChassisSwap.NewName(Car("redbull"), "toro_rosso"), model);
        Assert.Contains(erp.Resources, r => r.Identifier.EndsWith($"/idf/{model}.idf?render"));
        WheelBuild.MarkUnsupported(back);
        Assert.Contains(back.CarEntries, c => c.Folder + "/" == option && c.PreviousSwap is not null);
    }

    [Fact]
    public void CarsThatCantBeTheChassisAreRefused()
    {
        var tpl = new SerpsTemplate(Env.Template(RacingBulls));
        var team = TemplateTeams.Of(tpl);
        Assert.NotNull(SwapBuild.ChassisProblem(tpl, null, team, Car("toro_rosso")));          // its own car
        Assert.NotNull(SwapBuild.ChassisProblem(tpl, null, team, Car("redbull@2025")));        // another season
        Assert.Null(SwapBuild.ChassisProblem(tpl, null, team, Car("toro_rosso_suzuka_26")));   // its own special livery is a car of its own
        Assert.Null(SwapBuild.ChassisProblem(tpl, null, team, Car("redbull")));
        var myTeam = new SerpsTemplate(Env.Template("2026 My Team - SERPs Template v1.1.serps"));
        Assert.Null(SwapBuild.ChassisProblem(myTeam, null, TemplateTeams.Of(myTeam), Car("redbull")));    // My Team takes any 2026 car (MyTeamChassisTests)
        Assert.NotNull(SwapBuild.ChassisProblem(myTeam, null, TemplateTeams.Of(myTeam), Car("ferrari@2025")));
    }
}
