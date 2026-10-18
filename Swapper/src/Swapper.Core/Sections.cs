using System.Text.RegularExpressions;
using WheelSwap.Core;

namespace Swapper.Core;

/// <summary>
/// What a build does to the files of the template or project it was made from (all paths are the source's): files changed
/// where they are, moved, added (new sections) or left out (an earlier section a new one replaces), and in a project, the
/// parts switched on and off.
/// </summary>
public sealed class OutputPlan
{
    public Dictionary<string, byte[]> Replaced { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Renamed { get; } = new(StringComparer.Ordinal);
    public List<OutEntry> Added { get; } = [];
    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);
    /// <summary>A project's parts to switch on (output paths) and off (source paths).</summary>
    public HashSet<string> SwitchOn { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SwitchOff { get; } = new(StringComparer.Ordinal);
    /// <summary>The new sections (folders) and the one each was made from.</summary>
    public List<(string Folder, string From)> NewSections { get; } = [];
}

/// <summary>
/// Swaps go into a section of their own: a chassis option that gets a new wheel is copied, with everything in its folder
/// (the livery, the numbers), into a new folder named after the wheel, and the copy gets the wheel. The template's or
/// project's own sections are never changed. The new section has the same order and conflict group as the one it was
/// made from, so it sits right after it as an alternative; in a project it is switched on and the other chassis options
/// of its group are switched off (only one of a group can be on).
/// </summary>
public static class Sections
{
    // a template's folders and parts: "NN--GG--name" (order, conflict group; 00 = none)
    static readonly Regex Prefix = new(@"^(\d\d)--(\d\d)--(.+)$", RegexOptions.Compiled);

    static string Parent(string folder) => folder[..(folder.LastIndexOf('/') + 1)];
    static string Leaf(string folder) => folder[(folder.LastIndexOf('/') + 1)..];

    /// <summary>The conflict group of a folder (null = none), with its parent: folders of one parent and group are alternatives.</summary>
    static (string Parent, string Group)? GroupOf(string folder) =>
        Prefix.Match(Leaf(folder)) is { Success: true } m && m.Groups[2].Value != "00" ? (Parent(folder), m.Groups[2].Value) : null;

    static string FolderOf(string zip) => zip.Contains('/') ? zip[..zip.TrimEnd('/').LastIndexOf('/')] : "";

    /// <summary>The plan for a wheel build: each built option in a new section (or, for a section this app made, changed
    /// where it is and named after the new wheel).</summary>
    public static OutputPlan ForWheel(SerpsTemplate tpl, SimplifiedProject? project, BuildResult res, bool toProject, SwapLog log, OutputPlan? into = null) =>
        ForOptions(tpl, project, res.Entries.Select(b => b.Entry.Folder), res.Replaced, GameData.WheelSuffix(res.DonorToken), null, null, toProject, log, into);

    /// <summary>
    /// The plan for the template's own chassis options given a new wheel and / or new rims: each changed option in a new
    /// section named after them ("Default Chassis - 26 McLaren Wheel - Ferrari Rims"; a section this app made is changed
    /// where it is and renamed, keeping the ending of what it isn't given again). <paramref name="replaced"/>: the options'
    /// new ERPs (and any other file of their folders the build changed).
    /// </summary>
    public static OutputPlan ForOptions(SerpsTemplate tpl, SimplifiedProject? project, IEnumerable<string> folders, IReadOnlyDictionary<string, byte[]> replaced,
                                        string? wheelSuffix, string? rimsSuffix, string? engineSuffix, bool toProject, SwapLog log, OutputPlan? into = null)
    {
        var plan = into ?? new OutputPlan();
        var names = tpl.EntryNames;
        var parts = new[] { wheelSuffix is null ? null : "the new wheel", rimsSuffix is null ? null : "the new rims",
                            engineSuffix is null ? null : "the new engine sound" }.OfType<string>().ToList();
        var what = parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        foreach (var folder in folders.Distinct())
        {
            var changed = replaced.Keys.Where(k => FolderOf(k) == folder).ToList();
            var m = Prefix.Match(Leaf(folder));
            // a flat template (ERPs at the top) or an older project's default / enabled folders: no sections to add to
            if (!folder.StartsWith("ERPs/", StringComparison.Ordinal) || !m.Success)
            {
                foreach (var k in changed) plan.Replaced[k] = replaced[k];
                log.Info($"{(folder.Length == 0 ? "This template has no option folders" : $"'{folder}' is not an option folder")}: {what} go{(what.Contains(" and ") ? "" : "es")} onto the chassis where it is.");
                continue;
            }
            var display = m.Groups[3].Value;
            var (own, oldWheel, oldRims, oldEngine) = Endings(display);
            string? wheelEnd = wheelSuffix ?? oldWheel, rimsEnd = rimsSuffix ?? oldRims, engineEnd = engineSuffix ?? oldEngine;
            var name = own + (wheelEnd is null ? "" : " - " + wheelEnd) + (rimsEnd is null ? "" : " - " + rimsEnd) + (engineEnd is null ? "" : " - " + engineEnd);
            var newLeaf = $"{m.Groups[1].Value}--{m.Groups[2].Value}--{name}";
            var inFolder = names.Where(n => n.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
            if (own != display)
            {
                // a section this app made: it is ours to change, and it is named after what it now has
                foreach (var k in changed) plan.Replaced[k] = replaced[k];
                if (Leaf(folder) != newLeaf)
                {
                    foreach (var n in inFolder) plan.Renamed[n] = Parent(folder) + newLeaf + n[folder.Length..];
                    log.Info($"Section '{display}' -> '{name}' (made by this app, so changed where it is).");
                }
                continue;
            }
            var newFolder = Parent(folder) + newLeaf;
            // the same option given the same before: that section is replaced
            var earlier = names.Where(n => n == newFolder + "/" || n.StartsWith(newFolder + "/", StringComparison.Ordinal)).ToList();
            plan.Removed.UnionWith(earlier);
            plan.Added.Add(new OutEntry(newFolder + "/"));
            foreach (var n in inFolder.Where(n => !n.EndsWith('/')))
                plan.Added.Add(new OutEntry(newFolder + n[folder.Length..], n, replaced.GetValueOrDefault(n)));
            plan.NewSections.Add((newFolder, folder));
            log.Info($"New section '{name}': a copy of '{display}' with {what}{(earlier.Count > 0 ? " (replaces the earlier one)" : "")}; '{display}' is kept as it is.");
        }
        if (into is null) Switch(tpl, project, plan, toProject, log);
        return plan;
    }

    static readonly Regex RimsEnding = new(@" - ([^-]+? Rims)$", RegexOptions.Compiled);
    static readonly Regex EngineEnding = new(@" - ([^-()]+? Engine \([^()]+ \d\d\))$", RegexOptions.Compiled);

    /// <summary>A section's name split into the option it was made from and the endings this app gave it: the wheel's
    /// ("26 McLaren Wheel"), the rims' ("Ferrari Rims") and the engine sound's ("Ferrari Engine (Haas 26)"), null where it has none.</summary>
    public static (string Own, string? Wheel, string? Rims, string? Engine) Endings(string display)
    {
        string? rims = null, engine = null;
        if (EngineEnding.Match(display) is { Success: true } em) { engine = em.Groups[1].Value; display = display[..em.Index]; }
        if (RimsEnding.Match(display) is { Success: true } rm) { rims = rm.Groups[1].Value; display = display[..rm.Index]; }
        var own = GameData.StripWheelSuffix(display);
        return (own, own == display ? null : display[(own.Length + 3)..], rims, engine);
    }

    /// <summary>The chassis options' conflict group (every template's "Livery - ... Chassis" folders).</summary>
    public const string ChassisGroup = "01";

    /// <summary>
    /// The plan for a chassis swap: the swapped car is a new chassis option of its own, "NN--01--Livery - X Chassis" (with
    /// " - 26 Y Wheel" when a new wheel was built onto it), at the order of the last chassis option so it sits right after
    /// them, as Team Simplified's swapper places it, holding "01--00--&lt;template token&gt;_car_swapped_livery-and-chassis.erp".
    /// The template's options are kept as they are; the same car (and wheel) again replaces its earlier section. In a
    /// project the new option is switched on and the others off.
    /// </summary>
    public static OutputPlan ForChassis(SerpsTemplate tpl, SimplifiedProject? project, ChassisDonor donor, string? wheel, ChassisDonor? rims, EngineSound? engine, byte[] erp, bool toProject, SwapLog log, OutputPlan? into = null)
    {
        if (ChassisProblem(tpl, project) is { } problem) throw new InvalidOperationException(problem);
        var plan = into ?? new OutputPlan();
        var names = tpl.EntryNames;
        var options = ChassisOptions(tpl);
        var (folder, part) = ChassisPlace(tpl, donor, wheel, rims, engine);
        var earlier = names.Where(n => n == folder + "/" || n.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
        plan.Removed.UnionWith(earlier);
        plan.Added.Add(new OutEntry(folder + "/"));
        plan.Added.Add(new OutEntry(part, null, erp));
        // switched on in place of the option that is on (else the first): its group's other options go off
        var from = options.FirstOrDefault(f => f != folder && names.Any(n => FolderOf(n) == f && (project?.IsOn(n) ?? false)))
                   ?? options.FirstOrDefault(f => f != folder) ?? options[0];
        plan.NewSections.Add((folder, from));
        log.Info($"New chassis option '{Leaf(folder)[8..]}'{(earlier.Count > 0 ? " (replaces the earlier one)" : "")}; the template's chassis options are kept as they are.");
        if (into is null) Switch(tpl, project, plan, toProject, log);
        return plan;
    }

    /// <summary>The driverwear's conflict group (every template's "Driverwear" folder).</summary>
    public const string SuitGroup = "02";

    /// <summary>
    /// The plan for a suit swap: the swapped driverwear is a section of its own beside the template's, "NN--02--Driverwear -
    /// SET" (the order of the template's driverwear, as Team Simplified's swapper places it) holding
    /// "01--00--&lt;template token&gt;_driverwear_swapped.erp". The template's driverwear is kept as it is; the same set again
    /// replaces its earlier section. In a project the new section is switched on and the template's driverwear off.
    /// </summary>
    public static OutputPlan ForSuits(SerpsTemplate tpl, SimplifiedProject? project, string set, byte[] erp, bool toProject, SwapLog log, OutputPlan? into = null)
    {
        if (SuitsProblem(tpl, project) is { } problem) throw new InvalidOperationException(problem);
        var plan = into ?? new OutputPlan();
        var names = tpl.EntryNames;
        var sections = OptionFolders(tpl, SuitGroup);
        var (folder, part) = SuitPlace(tpl, set);
        var earlier = names.Where(n => n == folder + "/" || n.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
        plan.Removed.UnionWith(earlier);
        plan.Added.Add(new OutEntry(folder + "/"));
        plan.Added.Add(new OutEntry(part, null, erp));
        var from = sections.FirstOrDefault(f => f != folder && names.Any(n => FolderOf(n) == f && (project?.IsOn(n) ?? false)))
                   ?? sections.FirstOrDefault(f => f != folder) ?? sections[0];
        plan.NewSections.Add((folder, from));
        log.Info($"New driverwear section '{Leaf(folder)[8..]}'{(earlier.Count > 0 ? " (replaces the earlier one)" : "")}; the template's driverwear is kept as it is.");
        if (into is null) Switch(tpl, project, plan, toProject, log);
        return plan;
    }

    /// <summary>Where swapped suits go in <paramref name="tpl"/>: the folder (no trailing slash) and the part.</summary>
    public static (string Folder, string Part) SuitPlace(SerpsTemplate tpl, string set)
    {
        var sections = OptionFolders(tpl, SuitGroup);
        if (sections.Count == 0) throw new InvalidOperationException(SuitsProblem(tpl, null));
        int order = sections.Max(f => int.Parse(Leaf(f)[..2]));
        var name = $"Driverwear - {set}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        var folder = $"ERPs/{order:00}--{SuitGroup}--{name}";
        return (folder, $"{folder}/01--00--{(NamePrefix(tpl) is { } p ? p + "_" : "")}driverwear_swapped.erp");
    }

    /// <summary>Why suits can't be added to <paramref name="tpl"/> as a section of their own (null = they can).</summary>
    public static string? SuitsProblem(SerpsTemplate tpl, SimplifiedProject? project) =>
        project is { Legacy: true } ? "This is an older Simplified Studio project (default / enabled folders): open it in Simplified Studio and save it once, then swap the suits."
        : OptionFolders(tpl, SuitGroup).Count == 0 ? "This template has no driverwear folder (ERPs/NN--02--Driverwear) to add the suits beside."
        : null;

    /// <summary>A plan's switching, once every swap is in it (see <see cref="Switch"/>).</summary>
    public static void Finish(SerpsTemplate tpl, SimplifiedProject? project, OutputPlan plan, bool toProject, SwapLog log) =>
        Switch(tpl, project, plan, toProject, log);

    /// <summary>Where a swapped chassis goes in <paramref name="tpl"/>: its folder (no trailing slash) and its part.</summary>
    public static (string Folder, string Part) ChassisPlace(SerpsTemplate tpl, ChassisDonor donor, string? wheel, ChassisDonor? rims = null, EngineSound? engine = null)
    {
        var options = ChassisOptions(tpl);
        if (options.Count == 0) throw new InvalidOperationException(ChassisProblem(tpl, null));
        int order = options.Max(f => int.Parse(Leaf(f)[..2]));
        var folder = $"ERPs/{order:00}--{ChassisGroup}--{ChassisFolderName(donor)}{(wheel is null ? "" : " - " + GameData.WheelSuffix(wheel))}{(rims is null ? "" : " - " + RimSwap.Suffix(rims))}{(engine is null ? "" : " - " + EngineSwap.Suffix(engine))}";
        return (folder, $"{folder}/01--00--{(NamePrefix(tpl) is { } p ? p + "_" : "")}car_swapped_livery-and-chassis.erp");
    }

    /// <summary>Why a chassis can't be added to <paramref name="tpl"/> as an option of its own (null = it can).</summary>
    public static string? ChassisProblem(SerpsTemplate tpl, SimplifiedProject? project) =>
        project is { Legacy: true } ? "This is an older Simplified Studio project (default / enabled folders): open it in Simplified Studio and save it once, then swap the chassis."
        : ChassisOptions(tpl).Count == 0 ? "This template has no chassis option folders (ERPs/NN--01--Livery - ... Chassis) to add a chassis option beside."
        : null;

    /// <summary>The template's chassis option folders (zip paths, no trailing slash), in order.</summary>
    static List<string> ChassisOptions(SerpsTemplate tpl) => OptionFolders(tpl, ChassisGroup);

    /// <summary>The template's top folders of one conflict group (zip paths, no trailing slash), in order.</summary>
    static List<string> OptionFolders(SerpsTemplate tpl, string group) =>
        tpl.EntryNames.Select(n => n.EndsWith('/') ? n.TrimEnd('/') : FolderOf(n))
           .Where(f => f.StartsWith("ERPs/", StringComparison.Ordinal) && Parent(f) == "ERPs/" && GroupOf(f)?.Group == group)
           .Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>The option's name: "Livery - Red Bull Chassis", "Livery - McLaren Silverstone 26 Chassis".</summary>
    public static string ChassisFolderName(ChassisDonor donor)
    {
        var name = $"Livery - {donor.Label.Replace(" · ", " ")} Chassis";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        while (name.Contains("--")) name = name.Replace("--", "-");
        return name.Trim();
    }

    /// <summary>The token a template's parts are named with ("26-racingbulls"), the most common; null when none is.</summary>
    static string? NamePrefix(SerpsTemplate tpl) =>
        tpl.EntryNames.Where(n => !n.EndsWith('/')).Select(n => Regex.Match(Leaf(n), @"^\d\d--\d\d--(.+?)_"))
           .Where(m => m.Success).GroupBy(m => m.Groups[1].Value).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    /// <summary>
    /// What a project switches on: in each conflict group, the new section made from the option that was on (else the first
    /// one made) goes on, with the parts its source had on (or all of its parts), and every other part of that group goes
    /// off. A new project made from a template has the new sections (one per group) and the parts changed in place on.
    /// </summary>
    static void Switch(SerpsTemplate tpl, SimplifiedProject? project, OutputPlan plan, bool toProject, SwapLog log)
    {
        if (!toProject || project is { Legacy: true }) return;
        bool On(string zip) => project?.IsOn(zip) ?? false;
        foreach (var group in plan.NewSections.GroupBy(s => GroupOf(s.From)?.ToString() ?? s.Folder))
        {
            var pick = group.FirstOrDefault(s => tpl.EntryNames.Any(n => FolderOf(n) == s.From && On(n)));
            if (pick == default) pick = group.First();
            var parts = plan.Added.Where(a => FolderOf(a.Name) == pick.Folder && !a.Name.EndsWith('/')).ToList();
            var wasOn = parts.Where(a => a.From is not null && On(a.From)).ToList();
            foreach (var a in wasOn.Count > 0 ? wasOn : parts) plan.SwitchOn.Add(a.Name);
            if (project is not null)
            {
                var g = GroupOf(pick.From);
                foreach (var n in tpl.EntryNames.Where(n => On(n) && (g is null ? FolderOf(n) == pick.From : GroupOf(FolderOf(n)) == g)))
                    plan.SwitchOff.Add(n);
            }
            foreach (var other in group.Where(s => s != pick))
                log.Info($"'{Leaf(other.Folder)[8..]}' is added switched off: only one option of a group can be on (switch it on in Simplified Studio).");
            log.Info($"'{Leaf(pick.Folder)[8..]}' is switched on{(project is null ? "" : " and the others of its group are switched off (they are kept as they are)")}.");
        }
    }
}
