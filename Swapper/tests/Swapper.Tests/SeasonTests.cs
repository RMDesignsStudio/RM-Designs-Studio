using System.IO.Compression;
using Swapper.Core;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// 2025 cars take steering wheels in Swapper (any 2026 or 2025 wheel): the build uses the 2025 in-car driver files, and a
/// wheel of the other season carries its clips (and their string table) in the entry. The Steering Wheel Swapper app keeps
/// 2026 cars only (the engine's default).
/// </summary>
public class SeasonTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;

    /// <summary>A template whose one chassis option is the game's 2025 car (as a 2025 template has it).</summary>
    static SerpsTemplate Template2025(string team)
    {
        var path = Path.Combine(Out, $"2025 {team} - test template {Guid.NewGuid():N}.serps");   // its own: the tests run side by side
        var comment = ZipFile.OpenRead(Env.Template("2026 Ferrari - SERPs Template v1.1.serps")).Comment;
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            z.CreateEntry("ERPs/");
            z.CreateEntry("ERPs/02--01--Livery - Default Chassis/");
            z.CreateEntryFromFile(Env.Game().TeamErp(team, 2025), $"ERPs/02--01--Livery - Default Chassis/01--00--25-{team}_car_default_livery-and-chassis.erp", CompressionLevel.NoCompression);
            z.Comment = comment;
        }
        return new SerpsTemplate(path);
    }

    [Fact]
    public void A2026WheelGoesOnA2025CarWithItsClips()
    {
        var tpl = Template2025("ferrari");
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var entry = Assert.Single(tpl.CarEntries);
        Assert.Equal(2025, entry.Season);
        Assert.Null(entry.Problem);
        var log = new SwapLog();
        var res = tpl.Build(Env.Game(), "redbull", [entry], null, log, carPlacement: true, anySeason: true);   // verified
        var erp = ErpX.Load(res.Replaced[entry.ZipPath]);
        Assert.Contains(erp.Resources, r => r.ResourceType == "AnimClip" && r.Identifier.Contains("/idf/driver_in_car_2026_redbull.idf?context=animation"));
        Assert.Contains(erp.Resources, r => r.ResourceType == "ACStringTable");
        Assert.Contains(erp.Resources, r => r.Identifier.EndsWith("driver_in_car_2025_ferrari.idf?context=animset"));
    }

    [Fact]
    public void A2025WheelOnA2025CarCarriesNoClips()
    {
        var tpl = Template2025("mercedes");
        WheelBuild.MarkUnsupported(tpl, anySeason: true);
        var entry = Assert.Single(tpl.CarEntries);
        var res = tpl.Build(Env.Game(), "ferrari@2025", [entry], null, new SwapLog(), carPlacement: true, anySeason: true);
        Assert.DoesNotContain(ErpX.Load(res.Replaced[entry.ZipPath]).Resources, r => r.ResourceType == "AnimClip");
    }

    [Fact]
    public void TheSteeringWheelSwapperKeeps2026CarsOnly()
    {
        var tpl = Template2025("ferrari");
        WheelBuild.MarkUnsupported(tpl);
        Assert.NotNull(Assert.Single(tpl.CarEntries).Problem);
    }
}
