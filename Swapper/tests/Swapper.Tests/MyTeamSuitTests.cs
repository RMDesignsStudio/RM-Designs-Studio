using System.IO.Compression;
using EgoEngineLibrary.Archive.Erp.Data;
using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// My Team suits: any suit set onto the My Team template's own driverwear part, matching the Williams Sparco suits RM Designs
/// built by hand and proved in game (LN4 Fusion); and each F1 suit set's maker.
/// </summary>
public class MyTeamSuitTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;
    const string MyTeam = "2026 My Team - SERPs Template v1.1.serps";

    static SuitSet Set(string name) => Catalog.Suits(Env.Game()).First(s => s.Name == name);

    [Fact]
    public void AMyTeamTemplateGetsANewDriverwearSection()
    {
        var tpl = new SerpsTemplate(Env.Template(MyTeam));
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var team = TemplateTeams.Of(tpl);
        Assert.Equal("My Team", team!.Name);
        var own = MyTeamSuitSwap.TemplatePart(tpl);
        Assert.NotNull(own);
        Assert.Null(SwapBuild.SuitsProblem(tpl, null, team, Set("2026-williams")));
        var path = Path.Combine(Out, $"my team - williams suits {Guid.NewGuid():N}.serps");
        var outcome = SwapBuild.Run(Env.Game(), tpl, null, team, new(null, null, null, Set("2026-williams")), path, "test", new SwapLog());
        Assert.NotNull(outcome.MyTeamSuits);
        Assert.Equal(12, outcome.MyTeamSuits!.Models.Count);   // the six shell parts, each ?model and ?render
        var back = new SerpsTemplate(path);
        var part = back.EntryNames.Single(n => n.Contains("--02--Driverwear - 2026-williams/") && n.EndsWith("_driverwear_swapped.erp"));
        Assert.True(MyTeamSuitSwap.Verify(back.Get(part), outcome.MyTeamSuits) > 0);
        // the template's own driverwear is kept as it is
        Assert.True(tpl.Get(own!).AsSpan().SequenceEqual(back.Get(own!)));
    }

    /// <summary>Against the hand-built LN4 Fusion driverwear (only read): the same resources, and everything but the
    /// pictures byte for byte (the hand-built one's pictures were upscaled to 4096; ours are the game's full size).</summary>
    [Fact]
    public void TheSwapMatchesTheHandBuiltSparcoSuits()
    {
        var ln4 = Path.Combine(Env.ModsDir, "Patreon Mods", "My Team", "LN4 Fusion F1 Team", "LN4 Fusion.simplified");
        if (!File.Exists(ln4)) Assert.Skip($"not found: {ln4}");
        var tpl = new SerpsTemplate(Env.Template(MyTeam));
        using var z = ZipFile.OpenRead(ln4);
        using var ms = new MemoryStream();
        using (var s = z.GetEntry("ERPs/26-myteam_driverwear_sparco.erp")!.Open()) s.CopyTo(ms);
        var theirs = ErpX.Index(ErpX.Load(ms.ToArray()));
        var (bytes, _) = MyTeamSuitSwap.Run(Env.Game(), tpl.Get(MyTeamSuitSwap.TemplatePart(tpl)!), Set("2026-williams"), new SwapLog());
        var ours = ErpX.Index(ErpX.Load(bytes));
        Assert.Equal(theirs.Keys.Order(), ours.Keys.Order());
        foreach (var (id, a) in ours)
        {
            var b = theirs[id];
            if (a.ResourceType == "GfxSurfaceRes")
            {
                // the same picture slot: same format, full size (no external mipmaps)
                var srv = id[..^".image".Length];
                var (sa, sb) = (new ErpGfxSRVResource(), new ErpGfxSRVResource());
                sa.FromResource(ours[srv]); sb.FromResource(theirs[srv]);
                Assert.Equal(sb.SurfaceRes.Fragment0.ImageType, sa.SurfaceRes.Fragment0.ImageType);
                Assert.False(sa.SurfaceRes.HasMips);
                continue;
            }
            Assert.Equal(b.Fragments.Count, a.Fragments.Count);
            for (int f = 0; f < a.Fragments.Count; f++)
                Assert.True(a.Fragments[f].GetDataArray(true).AsSpan().SequenceEqual(b.Fragments[f].GetDataArray(true)), $"{id} fragment {f} differs");
        }
    }

    [Fact]
    public void SuitsShowTheirMaker()
    {
        Assert.Equal("Sparco", Set("2026-williams").Maker);
        Assert.Equal("Alpinestars", Set("2026-mclaren").Maker);
        Assert.Equal("Puma", Set("2025-ferrarimonza").Maker);   // a special livery is the team's
        Assert.Equal("adidas", Set("2026-sauber").Maker);
        Assert.Equal("OMP", Set("2025-zenith").Maker);           // APXGP
        Assert.Equal("Puma", Set("2025-williamsgulf").Maker);
        Assert.Equal("Sabelt", Set("2026-f2-camposracing").Maker);
        Assert.Null(Set("2025-konnersport").Maker);              // the story team: none
        // every other set in the game has its maker (a missing one is a table entry not matching a set name)
        Assert.All(Catalog.Suits(Env.Game()).Where(s => s.Key != "konnersport"), s => Assert.True(s.Maker is not null, s.Name));
    }
}
