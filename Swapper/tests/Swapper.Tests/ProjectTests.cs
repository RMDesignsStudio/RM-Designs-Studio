using System.IO.Compression;
using System.Text.Json.Nodes;
using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Tests;

/// <summary>
/// Saving: a chassis option that gets a new wheel becomes a new section beside the original (which is kept byte for
/// byte) in the new .serps. Simplified Studio projects (.simplified) are neither opened nor written.
/// </summary>
public class ProjectTests
{
    static readonly string Out = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SwapperTests", Environment.ProcessId.ToString())).FullName;

    /// <summary>Build a wheel onto chassis options and save it, as the app does.</summary>
    static string Save(SerpsTemplate tpl, IReadOnlyList<CarEntry> entries, string wheel, string outPath)
    {
        var log = new SwapLog();
        var res = tpl.Build(Env.Game(), wheel, entries, null, log);
        var project = SimplifiedProject.IsProject(tpl.SourcePath) ? SimplifiedProject.Read(tpl.SourcePath) : null;
        var plan = Sections.ForWheel(tpl, project, res, false, log);
        TemplateOutput.Write(tpl, plan, outPath, "test", log);
        return outPath;
    }

    const string Ferrari = "2026 Ferrari - SERPs Template v1.1.serps";
    const string FerrariDefault = "ERPs/02--01--Livery - Default Chassis/";
    const string FerrariNew = "ERPs/02--01--Livery - Default Chassis - 26 Red Bull Wheel/";

    [Fact]
    public void ATemplateGetsANewSectionAndKeepsItsOwn()
    {
        var tpl = new SerpsTemplate(Env.Template(Ferrari));
        var path = Save(tpl, [tpl.CarEntries[0]], "redbull", Path.Combine(Out, "ferrari.serps"));
        var back = new SerpsTemplate(path);
        // every file of the template is there unchanged (the save checks the bytes) and in its order, plus the new section's,
        // which sit where SERPs keeps them: the folder right after its original's folder, its files right after the original's
        Assert.Equal(tpl.EntryNames, back.EntryNames.Where(n => !n.StartsWith(FerrariNew)));
        Assert.Equal([FerrariNew, FerrariNew + "01--00--26-ferrari_car_default_livery-and-chassis.erp", FerrariNew + "02--00--26-ferrari_car_default_numbers.erp"],
                     back.EntryNames.Where(n => n.StartsWith(FerrariNew)));
        var names = back.EntryNames.ToList();
        Assert.Equal(names.IndexOf(FerrariDefault) + 1, names.IndexOf(FerrariNew));
        Assert.Equal(names.IndexOf(FerrariDefault + "02--00--26-ferrari_car_default_numbers.erp") + 1, names.IndexOf(FerrariNew + "01--00--26-ferrari_car_default_livery-and-chassis.erp"));
        Assert.Equal(tpl.Comment, back.Comment);
        Assert.Contains(back.CarEntries, c => c.Folder + "/" == FerrariNew && c.PreviousSwap is not null);
        Assert.Contains(back.CarEntries, c => c.Folder + "/" == FerrariDefault && c.PreviousSwap is null);
    }

    [Fact]
    public void TwoOptionsGiveTwoSections()
    {
        var tpl = new SerpsTemplate(Env.Template(Ferrari));
        var path = Save(tpl, tpl.CarEntries, "redbull", Path.Combine(Out, "ferrari both.serps"));
        var names = new SerpsTemplate(path).EntryNames;
        Assert.Contains(names, n => n.StartsWith(FerrariNew));
        Assert.Contains(names, n => n.StartsWith("ERPs/03--01--Livery - FOM Chassis - 26 Red Bull Wheel/"));
    }

    [Fact]
    public void ASectionThisAppMadeChangesWhereItIs()
    {
        var tpl = new SerpsTemplate(Env.Template(Ferrari));
        var first = Save(tpl, [tpl.CarEntries[0]], "redbull", Path.Combine(Out, "ferrari - red bull.serps"));
        // that section given another wheel: it is ours, so it changes where it is and takes the new wheel's name
        var tpl2 = new SerpsTemplate(first);
        WheelBuild.MarkUnsupported(tpl2);
        var second = Save(tpl2, [tpl2.CarEntries.First(c => c.Folder.EndsWith("26 Red Bull Wheel"))], "mclaren", Path.Combine(Out, "ferrari - mclaren.serps"));
        var names = new SerpsTemplate(second).EntryNames;
        Assert.DoesNotContain(names, n => n.Contains("Red Bull Wheel"));
        Assert.Contains(names, n => n.StartsWith("ERPs/02--01--Livery - Default Chassis - 26 McLaren Wheel/"));
        Assert.Contains(names, n => n.StartsWith(FerrariDefault));
        // the original option given the same wheel again: the earlier section is replaced, not doubled
        var tpl3 = new SerpsTemplate(second);
        WheelBuild.MarkUnsupported(tpl3);
        var third = Save(tpl3, [tpl3.CarEntries.First(c => c.Folder + "/" == FerrariDefault)], "mclaren", Path.Combine(Out, "ferrari - again.serps"));
        Assert.Single(new SerpsTemplate(third).EntryNames, n => n == "ERPs/02--01--Livery - Default Chassis - 26 McLaren Wheel/");
    }

    [Fact]
    public void ProjectsAreNeitherOpenedNorWritten()
    {
        // Team Simplified's wish: projects stay Simplified Studio's (it exports them as a .serps, which Swapper opens)
        var tpl = new SerpsTemplate(Env.Template(Ferrari));
        Assert.NotNull(TemplateOutput.Problem(tpl, Path.Combine(Out, "x.simplified")));
        Assert.Null(TemplateOutput.Problem(tpl, Path.Combine(Out, "x.serps")));
        Assert.NotNull(TemplateOutput.Problem(tpl, tpl.SourcePath));   // never over the file opened
        Assert.Throws<InvalidOperationException>(() => Save(tpl, [tpl.CarEntries[0]], "redbull", Path.Combine(Out, "ferrari.simplified")));
        Assert.False(File.Exists(Path.Combine(Out, "ferrari.simplified")));
        var src = Env.Project(@"Overtake.gg Mods\Cadillac\Gainbridge Cadillac Formula 1 Team\v1.1\gainbridgecadillac.simplified");
        var proj = new SerpsTemplate(src);
        Assert.Equal(TemplateOutput.ProjectNote, TemplateOutput.Problem(proj, Path.Combine(Out, "gainbridge.serps")));
        Assert.Throws<InvalidOperationException>(() => SwapBuild.Run(Env.Game(), proj, null, TemplateTeams.Of(proj), new(null, null, null, Catalog.Suits(Env.Game()).First(x => x.Name == "2025-haasmontreal")),
                                                                     Path.Combine(Out, "gainbridge.serps"), "test", new SwapLog()));
    }
}
