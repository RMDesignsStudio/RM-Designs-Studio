using System.IO.Compression;
using System.Text.Json.Nodes;
using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// The suit swap (Team Simplified's driverwear swap, ported): the driverwear it makes matches the swaps the user made with
/// their app resource for resource, and it goes into a new template or project as a driverwear section of its own; any
/// mix of chassis, wheel and suits builds into one file.
/// </summary>
public class SuitTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;

    static SuitSet Set(string name) => Catalog.Suits(Env.Game()).First(s => s.Name == name);
    static ChassisDonor Car(string id) => Catalog.Chassis(Env.Game()).First(c => c.Id == id);

    static string[] Enabled(string path)
    {
        using var z = ZipFile.OpenRead(path);
        using var s = z.GetEntry("info.json")!.Open();
        return JsonNode.Parse(s)!["enabled"]!.AsArray().Select(x => x!.GetValue<string>()).Order().ToArray();
    }

    static string Build(string source, SwapBuild.Picks picks, string outPath)
    {
        var tpl = new SerpsTemplate(source);
        WheelBuild.MarkUnsupported(tpl);
        var project = SimplifiedProject.IsProject(source) ? SimplifiedProject.Read(source) : null;
        // a wheel on the template's own options: the ones switched on (all of a template's)
        if (picks is { Chassis: null, Wheel: not null, Entries: null })
            picks = picks with { Entries = tpl.CarEntries.Where(e => e.Problem is null && (project?.IsOn(e.ZipPath) ?? true)).ToList() };
        SwapBuild.Run(Env.Game(), tpl, project, project is null ? TemplateTeams.Of(tpl) : TemplateTeams.Of(project), picks, outPath, "test", new SwapLog());
        return outPath;
    }

    [Theory]
    [InlineData("2026 Aston Martin - SERPs Template v1.1 - Williams Sparco Suits.serps", "2026-williams", "2026-forceindia")]
    [InlineData("2026 McLaren - SERPs Template v1.1 - Monaco Chassis & Puma Suits.serps", "2026-ferrari", "2026-mclaren")]
    public void TheSwapMatchesTeamSimplifiedsAppResourceForResource(string golden, string wear, string team)
    {
        var path = Path.Combine(Env.TemplateDir, "Swapped Templates", golden);
        if (!File.Exists(path)) Assert.Skip($"not found: {path}");
        using var z = ZipFile.OpenRead(path);
        using var ms = new MemoryStream();
        using (var s = z.Entries.First(e => e.FullName.Contains("_driverwear_swapped")).Open()) s.CopyTo(ms);
        var theirs = ErpX.Index(ErpX.Load(ms.ToArray()));
        var (bytes, result) = SuitSwap.Run(Env.Game(), wear, team, new SwapLog());
        var ours = ErpX.Index(ErpX.Load(bytes));
        Assert.Equal(theirs.Keys.Order(), ours.Keys.Order());
        foreach (var (id, a) in ours)
        {
            var b = theirs[id];
            Assert.Equal(b.Fragments.Count, a.Fragments.Count);
            for (int f = 0; f < a.Fragments.Count; f++)
                Assert.True(a.Fragments[f].GetDataArray(true).AsSpan().SequenceEqual(b.Fragments[f].GetDataArray(true)), $"{id} fragment {f} differs");
        }
        Assert.Equal(2, result.Drivers.Count);
        Assert.Empty(result.Warnings);
        Assert.True(SuitSwap.Verify(bytes, result) > 20);
    }

    [Fact]
    public void ATemplateGetsTheSuitsAsADriverwearSectionOfItsOwn()
    {
        const string section = "ERPs/04--02--Driverwear - 2026-williams/";
        var src = Env.Template("2026 Aston Martin - SERPs Template v1.1.serps");
        var tpl = new SerpsTemplate(src);
        var path = Build(src, new(null, null, null, Set("2026-williams")), Path.Combine(Out, "aston - williams suits.serps"));
        var names = new SerpsTemplate(path).EntryNames.ToList();
        Assert.Equal(tpl.EntryNames, names.Where(n => !n.StartsWith(section)));
        Assert.Equal(names.IndexOf("ERPs/04--02--Driverwear/") + 1, names.IndexOf(section));
        Assert.Contains(section + "01--00--26-astonmartin_driverwear_swapped.erp", names);
    }

    [Fact]
    public void ChassisWheelAndSuitsGoIntoOneTemplate()
    {
        var path = Build(Env.Template("2026 Racing Bulls - SERPs Template v1.1.serps"), new(Car("redbull"), "mclaren", null, Set("2026-mclaren")),
                         Path.Combine(Out, "racing bulls - all three.serps"));
        var names = new SerpsTemplate(path).EntryNames;
        Assert.Contains("ERPs/03--01--Livery - Red Bull Chassis - 26 McLaren Wheel/01--00--26-racingbulls_car_swapped_livery-and-chassis.erp", names);
        Assert.Contains("ERPs/04--02--Driverwear - 2026-mclaren/01--00--26-racingbulls_driverwear_swapped.erp", names);
    }

    [Fact]
    public void SuitsThatCantBeSwappedInAreRefused()
    {
        var tpl = new SerpsTemplate(Env.Template("2026 Ferrari - SERPs Template v1.1.serps"));
        var team = TemplateTeams.Of(tpl);
        Assert.NotNull(SwapBuild.SuitsProblem(tpl, null, team, Set("2026-ferrari")));   // its own
        Assert.Null(SwapBuild.SuitsProblem(tpl, null, team, Set("2024-williams")));    // another season is fine
        var myTeam = new SerpsTemplate(Env.Template("2026 My Team - SERPs Template v1.1.serps"));
        Assert.Null(SwapBuild.SuitsProblem(myTeam, null, TemplateTeams.Of(myTeam), Set("2026-ferrari")));   // My Team takes any set (MyTeamSuitTests)
    }
}
