using System.IO.Compression;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Core;

/// <summary>
/// One build, whatever is picked: a new chassis (a car from the game swapped into the template team's slot, see
/// <see cref="ChassisSwap"/>), a new steering wheel, new rims (<see cref="RimSwap"/>) and / or a new engine sound
/// (<see cref="EngineSwap"/>), built onto the new chassis when there is one, else onto the template's ticked chassis
/// options, new suits (<see cref="SuitSwap"/>; My Team's: <see cref="MyTeamSuitSwap"/>). Each swap is
/// checked, goes into a section of its own (<see cref="Sections"/>), and the new template or project is written and checked
/// again. The file opened is never changed.
/// </summary>
public static class SwapBuild
{
    /// <summary>What to build: any of the five (the wheel donor id; the rims' car; the engine sound; <paramref name="Entries"/>:
    /// the template's options the wheel, rims and engine sound go onto when there's no new chassis).</summary>
    public sealed record Picks(ChassisDonor? Chassis, string? Wheel, IReadOnlyList<CarEntry>? Entries, SuitSet? Suits, ChassisDonor? Rims = null, EngineSound? Engine = null);

    /// <summary>What a build did: each swap (null when not picked), the checks made, the wheel report (null when none was
    /// asked for or it couldn't be written: the log says why).</summary>
    public sealed record Outcome(ChassisSwapResult? Chassis, BuildResult? Wheel, SuitSwapResult? Suits, int Checks, string? ReportPath, List<RimSwapResult>? Rims = null,
                                 MyTeamSuitResult? MyTeamSuits = null, MyTeamChassisResult? MyTeamChassis = null);

    static int? Season(SerpsTemplate tpl) =>
        tpl.CarEntries.GroupBy(c => c.Season).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();

    /// <summary>Why <paramref name="donor"/> can't be the new chassis of <paramref name="tpl"/> (null = it can).</summary>
    public static string? ChassisProblem(SerpsTemplate tpl, SimplifiedProject? project, TemplateTeams.Team? team, ChassisDonor donor)
    {
        if (team is null) return "This template's team isn't recognised, so there is no slot to put a chassis in.";
        if (!team.ChassisSwapWorks) return $"Not for {team.Name}: the game doesn't load a swapped chassis in its slots.";
        if (team.IsMyTeam && donor.Team == MyTeamChassis.Slot) return "That is the FOM car, My Team's own: pick another car.";
        if (donor.Team == team.Folders[0] && donor.Livery is null) return $"That is {team.Name}'s own car: pick another car.";
        if (Season(tpl) is { } season && donor.Season != season)
            return $"That is a {donor.Season} car and this is a {season} template: only {season} cars can be its chassis.";
        return Sections.ChassisProblem(tpl, project);
    }

    /// <summary>Why <paramref name="set"/> can't be the new suits of <paramref name="tpl"/> (null = it can).</summary>
    public static string? SuitsProblem(SerpsTemplate tpl, SimplifiedProject? project, TemplateTeams.Team? team, SuitSet set)
    {
        if (team is null) return "This template's team isn't recognised, so it isn't known whose suits to replace.";
        if (!team.SuitSwapWorks) return $"Not for {team.Name}: its drivers wear the suit picked in game.";
        if (team.SuitSet is null && MyTeamSuitSwap.TemplatePart(tpl) is null) return MyTeamSuitSwap.NoPart;
        if (set.Name == team.SuitSet) return $"Those are {team.Name}'s own suits: pick another set.";
        if (set.ErpPath is null) return $"The game has no file for the {set.Name} suits.";
        return Sections.SuitsProblem(tpl, project);
    }

    /// <summary>Why <paramref name="rims"/> can't go on the cars of <paramref name="tpl"/> (null = they can).</summary>
    public static string? RimsProblem(SerpsTemplate tpl, ChassisDonor? chassis, ChassisDonor rims)
    {
        var season = chassis?.Season ?? Season(tpl);
        if (season is not null && rims.Season != season)
            return $"Those are {rims.Season} rims and this is a {season} car: only {season} cars' rims fit it.";
        return null;
    }

    /// <summary>The template's other ERPs that apply to a chassis option (its own folder, and folders holding no car), fresh.</summary>
    static List<EgoEngineLibrary.Archive.Erp.ErpFile> OthersOf(SerpsTemplate tpl, CarEntry entry, IReadOnlyDictionary<string, byte[]> replaced)
    {
        var carFolders = tpl.CarEntries.Select(c => c.Folder).ToHashSet();
        static string FolderOf(string n) => n.Contains('/') ? n[..n.LastIndexOf('/')] : "";
        var list = new List<EgoEngineLibrary.Archive.Erp.ErpFile>();
        foreach (var n in tpl.EntryNames.Where(n => n != entry.ZipPath && n.EndsWith(".erp", StringComparison.OrdinalIgnoreCase))
                                        .Where(n => FolderOf(n) == entry.Folder || !carFolders.Contains(FolderOf(n))))
            try { list.Add(ErpX.Load(replaced.GetValueOrDefault(n) ?? tpl.Get(n))); } catch (InvalidDataException) { }
        return list;
    }

    /// <summary>Build and save; the wheel's report is written next to the output when <paramref name="report"/>.</summary>
    public static Outcome Run(GameData game, SerpsTemplate tpl, SimplifiedProject? project, TemplateTeams.Team? team, Picks picks,
                              string outPath, string appVersion, SwapLog log, bool report = false)
    {
        if (picks.Chassis is null && picks.Wheel is null && picks.Suits is null && picks.Rims is null && picks.Engine is null) throw new InvalidOperationException("Nothing is picked to swap.");
        if (TemplateOutput.Problem(tpl, outPath) is { } op) throw new InvalidOperationException(op);
        if (picks.Chassis is { } c0 && ChassisProblem(tpl, project, team, c0) is { } cp) throw new InvalidOperationException(cp);
        if (picks.Chassis is { } c1 && picks.Wheel is { } w1 && ChassisView.WheelProblem(game, c1, w1) is { } wp) throw new InvalidOperationException("The steering wheel: " + wp + ".");
        if (picks.Suits is { } s0 && SuitsProblem(tpl, project, team, s0) is { } sp) throw new InvalidOperationException(sp);
        if (picks.Rims is { } r0 && RimsProblem(tpl, picks.Chassis, r0) is { } rp) throw new InvalidOperationException(rp);
        if (picks.Chassis is null && (picks.Wheel is not null || picks.Rims is not null || picks.Engine is not null) && picks.Entries is not { Count: > 0 })
            throw new InvalidOperationException($"Tick at least one chassis option to give the new {(picks.Wheel is not null ? "wheel" : picks.Rims is not null ? "rims" : "engine sound")}.");
        if (picks.Chassis is not null && picks.Engine is not null && team is { IsMyTeam: true })
            throw new InvalidOperationException("The engine sound: a My Team car's engine sound comes with the engine supplier picked in the game.");
        if (picks.Chassis is null && picks.Engine is not null && picks.Entries!.Select(EngineSwap.Problem).FirstOrDefault(p => p is not null) is { } ep)
            throw new InvalidOperationException("The engine sound: " + ep + ".");
        bool toProject = TemplateOutput.IsProject(outPath);
        var plan = new OutputPlan();
        int checks = 0;
        ChassisSwapResult? chassis = null;
        SuitSwapResult? suits = null;
        MyTeamSuitResult? myTeamSuits = null;
        MyTeamChassisResult? myTeamChassis = null;
        BuildResult? wheel = null;
        SerpsTemplate? wheelTpl = null;
        var rims = new List<RimSwapResult>();
        string? tmp = null;
        try
        {
            if (picks.Chassis is { } donor)
            {
                byte[] erp;
                bool myTeam = team!.IsMyTeam;
                if (myTeam)
                {
                    // My Team: the car goes into the FOM car's slot, drawn at its own height in every My Team slot (MyTeamChassis)
                    (erp, chassis, var mn) = MyTeamChassis.Start(game, donor, log);
                    checks += mn;
                    log.Info($"Swapped car checked: {mn} checks passed.");
                }
                else
                {
                    var slot = team.Folders[0];
                    (erp, chassis) = ChassisSwap.Run(game, donor, slot, ChassisSwap.NewName(donor, slot), log);
                    int n = ChassisSwap.Verify(game, erp, chassis, donor.Season);
                    checks += n;
                    log.Info($"Swapped car checked: {n} checks passed.");
                    // drawn at the donor's own height (the team's handling places it for the team's car)
                    (erp, var look) = CarLook.Apply(game, erp, donor, slot, log);
                    if (CarLook.Verify(game, erp, donor, slot, look) is var hn and > 0) { checks += hn; log.Info($"Height checked: {hn} checks passed."); }
                }
                if (picks.Wheel is { } w)
                {
                    // the wheel engine builds onto the swapped car where it will sit in the template (as onto any swapped
                    // chassis in a template), so it is given a template holding just that option (in the temp folder)
                    var (_, part) = Sections.ChassisPlace(tpl, donor, w, picks.Rims, picks.Engine);
                    tmp = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "RM Designs Studio", "Swapper", "build")).FullName,
                                       $"{Path.GetFileNameWithoutExtension(outPath)}.serps");
                    OneOption(tmp, part, erp);
                    wheelTpl = new SerpsTemplate(tmp);
                    WheelBuild.MarkUnsupported(wheelTpl, anySeason: true);
                    var entry = wheelTpl.CarEntries.SingleOrDefault() ?? throw new InvalidDataException("the swapped car isn't recognised as a chassis option");
                    if (entry.Problem is { } p) throw new InvalidDataException("the steering wheel can't go on the swapped car: " + p);
                    wheel = wheelTpl.Build(game, w, [entry], null, log, carPlacement: true, anySeason: true);   // built and verified in memory
                    checks += wheel.Verification.Sum(v => v.Checks);
                    erp = wheel.Replaced[part];
                }
                if (picks.Rims is { } rd)
                {
                    (erp, var rr) = RimSwap.Run(game, erp, donor.Season, rd, [], log);
                    int rn = RimSwap.Verify(game, erp, donor.Season, rr, []);
                    checks += rn;
                    rims.Add(rr);
                    log.Info($"Rims checked: {rn} checks passed.");
                }
                if (picks.Engine is { } en)
                {
                    erp = EngineSwap.Run(erp, en, log);
                    int en2 = EngineSwap.Verify(erp, en);
                    checks += en2;
                    log.Info($"Engine sound checked: {en2} checks passed.");
                }
                if (myTeam)
                {
                    // the engine slots show the car, and its binding answers every My Team livery, number and sticker
                    (erp, myTeamChassis) = MyTeamChassis.Finish(game, erp, chassis.NewName, donor.Season, log);
                    int mc = MyTeamChassis.Verify(game, erp, myTeamChassis, donor.Season);
                    checks += mc;
                    log.Info($"My Team car checked: {mc} checks passed.");
                }
                Sections.ForChassis(tpl, project, donor, picks.Wheel, picks.Rims, picks.Engine, erp, toProject, log, plan);
            }
            else if (picks.Wheel is not null || picks.Rims is not null || picks.Engine is not null)
            {
                // the template's ticked options: the wheel first (the engine's own build and checks), then the rims onto
                // what it made; each changed option becomes a section of its own
                var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                if (picks.Wheel is { } w)
                {
                    wheel = tpl.Build(game, w, picks.Entries!, null, log, carPlacement: true, anySeason: true);   // built and verified, nothing written yet
                    wheelTpl = tpl;
                    checks += wheel.Verification.Sum(v => v.Checks);
                    foreach (var (k, v) in wheel.Replaced) replaced[k] = v;
                }
                if (picks.Rims is { } rd)
                    foreach (var ce in picks.Entries!)
                    {
                        var bytes = replaced.GetValueOrDefault(ce.ZipPath) ?? tpl.Get(ce.ZipPath);
                        var (erp, rr) = RimSwap.Run(game, bytes, ce.Season, rd, OthersOf(tpl, ce, replaced), log, ce.Kind == EntryKind.MyTeamLivery);
                        int rn = RimSwap.Verify(game, erp, ce.Season, rr, OthersOf(tpl, ce, replaced));
                        checks += rn;
                        rims.Add(rr);
                        log.Info($"Rims on {ce.Title} checked: {rn} checks passed.");
                        replaced[ce.ZipPath] = erp;
                    }
                if (picks.Engine is { } eng)
                    foreach (var ce in picks.Entries!)
                    {
                        var erp = EngineSwap.Run(replaced.GetValueOrDefault(ce.ZipPath) ?? tpl.Get(ce.ZipPath), eng, log);
                        checks += EngineSwap.Verify(erp, eng);
                        replaced[ce.ZipPath] = erp;
                    }
                Sections.ForOptions(tpl, project, picks.Entries!.Select(e => e.Folder), replaced,
                                    picks.Wheel is { } ww ? GameData.WheelSuffix(ww) : null, picks.Rims is { } rr2 ? RimSwap.Suffix(rr2) : null,
                                    picks.Engine is { } ee ? EngineSwap.Suffix(ee) : null, toProject, log, plan);
            }
            if (picks.Suits is { } set && team!.SuitSet is null)
            {
                // My Team: the template's own driverwear part dressed in the set (see MyTeamSuitSwap)
                (var erp, myTeamSuits) = MyTeamSuitSwap.Run(game, tpl.Get(MyTeamSuitSwap.TemplatePart(tpl)!), set, log);
                int n = MyTeamSuitSwap.Verify(erp, myTeamSuits);
                checks += n;
                log.Info($"Swapped suits checked: {n} checks passed.");
                Sections.ForSuits(tpl, project, set.Name, erp, toProject, log, plan);
            }
            else if (picks.Suits is { } set2)
            {
                (var erp, suits) = SuitSwap.Run(game, set2.Name, team!.SuitSet!, log);
                int n = SuitSwap.Verify(erp, suits);
                checks += n;
                log.Info($"Swapped suits checked: {n} checks passed.");
                Sections.ForSuits(tpl, project, set2.Name, erp, toProject, log, plan);
            }
            Sections.Finish(tpl, project, plan, toProject, log);
            checks += TemplateOutput.Write(tpl, plan, outPath, appVersion, log);
            string? reportPath = null;
            if (report && wheel is not null)
                try { reportPath = BuildReport.Write(game, wheelTpl!, wheel, log, outPath); }
                catch (Exception ex) { log.Warn("The build is fine, but the wheel report could not be written: " + ex.Message); }
            return new Outcome(chassis, wheel, suits, checks, reportPath, rims, myTeamSuits, myTeamChassis);
        }
        finally { if (tmp is not null) try { File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>A template whose only file is <paramref name="erp"/> at <paramref name="part"/>.</summary>
    internal static void OneOption(string path, string part, byte[] erp)
    {
        var tmp = path + ".tmp";
        using (var zip = new ZipArchive(File.Create(tmp), ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry(part, CompressionLevel.NoCompression).Open();
            s.Write(erp);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
