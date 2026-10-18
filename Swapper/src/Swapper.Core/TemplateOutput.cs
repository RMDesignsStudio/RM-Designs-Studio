namespace Swapper.Core;

/// <summary>
/// Saving a build: always a new .serps template. Swapper neither opens nor writes Simplified Studio projects (.simplified):
/// Team Simplified asked that projects stay Simplified Studio's (it packages them into a .serps, which Swapper then opens).
/// The project code below is kept for reading only; <see cref="Problem"/> refuses both. What changes is an
/// <see cref="OutputPlan"/> (new sections, files changed or moved). Written next to the target, read back and checked,
/// and only then moved into place, so a failure never leaves a half-written or wrong file.
/// </summary>
public static class TemplateOutput
{
    /// <summary>The app id recorded in projects this app makes (<c>rm_app</c> in info.json).</summary>
    public const string AppId = "swapper";

    public static bool IsProject(string path) => SimplifiedProject.IsProject(path);

    /// <summary>Why <paramref name="outPath"/> can't be written from <paramref name="tpl"/> (null = it can).</summary>
    public static string? Problem(SerpsTemplate tpl, string outPath)
    {
        if (IsProject(tpl.SourcePath)) return ProjectNote;
        if (!outPath.EndsWith(".serps", StringComparison.OrdinalIgnoreCase))
            return "Swapper saves .serps templates: give the new file a .serps name.";
        if (Path.GetFullPath(outPath).Equals(Path.GetFullPath(tpl.SourcePath), StringComparison.OrdinalIgnoreCase))
            return "That is the file you opened: save the new one under another name.";
        return null;
    }

    /// <summary>What Swapper says to a Simplified Studio project.</summary>
    public const string ProjectNote = "Swapper opens .serps templates only. For a Simplified Studio project, export it as a .serps from Simplified Studio first, then open that here.";

    /// <summary>Write <paramref name="tpl"/> changed by <paramref name="plan"/> to <paramref name="outPath"/>. Returns the
    /// number of checks made on the file read back.</summary>
    public static int Write(SerpsTemplate tpl, OutputPlan plan, string outPath, string appVersion, SwapLog log)
    {
        if (Problem(tpl, outPath) is { } problem) throw new InvalidOperationException(problem);
        bool fromProject = IsProject(tpl.SourcePath), newProject = IsProject(outPath) && !fromProject;
        var project = fromProject ? SimplifiedProject.Read(tpl.SourcePath) : null;
        if (project is not null)
            foreach (var zip in plan.Replaced.Keys.Where(z => !project.IsOn(z) && !plan.SwitchOn.Contains(plan.Renamed.GetValueOrDefault(z, z))))
                log.Warn($"{zip.Split('/')[^1]} is switched off in this project: switch it on in Simplified Studio to use it.");
        var entries = Entries(tpl, plan, project, newProject, outPath, appVersion);
        var tmp = outPath + ".check";
        int checks;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            // a .serps keeps its zip comment (SERPs' injection target); a project made from a template names it in info.json
            SerpsWriter.Write(tpl, tmp, entries, newProject ? null : tpl.Comment);
            checks = Verify(tpl, tmp, IsProject(outPath), entries, plan, project, newProject);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
        try { File.Move(tmp, outPath, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException($"Could not save {Path.GetFileName(outPath)}: it is open in another program or read-only. Close it (or pick another name) and try again.", ex);
        }
        if (newProject) log.Info("Simplified project: the new parts are switched on.");
        log.Info($"Saved file checked: {checks} checks passed.");
        return checks;
    }

    /// <summary>The written file's entries: the source's own (changed, moved or left out by the plan), then the new
    /// sections; a new project gets its info.json first and only the files under ERPs/ (as Studio makes one).</summary>
    static List<OutEntry> Entries(SerpsTemplate tpl, OutputPlan plan, SimplifiedProject? project, bool newProject, string outPath, string appVersion)
    {
        string Out(string n)
        {
            var m = plan.Renamed.GetValueOrDefault(n, n);
            return newProject ? SimplifiedProject.ProjectPath(m)! : m;
        }
        var list = new List<OutEntry>();
        if (newProject)
        {
            var on = plan.SwitchOn.Concat(plan.Replaced.Keys.Where(k => SimplifiedProject.ProjectPath(k) is not null).Select(Out));
            list.Add(new OutEntry("info.json", null, SimplifiedProject.NewInfo(tpl, Path.GetFileNameWithoutExtension(outPath), AppId, appVersion, on)));
        }
        byte[]? info = project is { Legacy: false } && tpl.EntryNames.Contains("info.json")
            ? SimplifiedProject.UpdatedInfo(tpl.Get("info.json"), plan.Renamed, plan.SwitchOn, plan.SwitchOff) : null;
        foreach (var n in tpl.EntryNames)
        {
            if (plan.Removed.Contains(n) || newProject && SimplifiedProject.ProjectPath(n) is null) continue;
            list.Add(new OutEntry(Out(n), n, n == "info.json" && info is not null ? info : plan.Replaced.GetValueOrDefault(n)));
        }
        // new sections go where SERPs keeps them: folders among the folders, files among the files, in name order
        foreach (var a in plan.Added)
        {
            bool dir = a.Name.EndsWith('/');
            bool Erps(OutEntry e) => e.Name.StartsWith("ERPs/", StringComparison.Ordinal);
            int at = list.FindLastIndex(e => Erps(e) && e.Name.EndsWith('/') == dir && ComparePaths(e.Name, a.Name) < 0);
            if (at < 0) at = dir ? list.FindIndex(Erps) - 1 : list.FindLastIndex(e => Erps(e) && e.Name.EndsWith('/'));
            if (at < -1) at = list.Count - 1;
            list.Insert(at + 1, a);
        }
        return list;
    }

    /// <summary>Zip paths compared folder by folder ("X/" before "X - more/", as a folder listing has them).</summary>
    static int ComparePaths(string a, string b)
    {
        var x = a.Split('/'); var y = b.Split('/');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (string.CompareOrdinal(x[i], y[i]) is var c and not 0) return c;
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>
    /// The written file read back: exactly the planned files, in order, each holding exactly the planned bytes; every changed
    /// or new ERP loads; a .serps keeps its zip comment; in a project the parts switched on are on, those switched off are
    /// off, and every other part keeps its state.
    /// </summary>
    static int Verify(SerpsTemplate tpl, string path, bool toProject, List<OutEntry> entries, OutputPlan plan, SimplifiedProject? source, bool newProject)
    {
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) throw new InvalidDataException($"Saved file check failed: {what}"); }
        var back = new SerpsTemplate(path);
        Check(back.EntryNames.SequenceEqual(entries.Select(e => e.Name)), "the list of files is not the planned one");
        foreach (var e in entries.Where(e => !e.Name.EndsWith('/')))
            Check(back.Get(e.Name).AsSpan().SequenceEqual(e.Data ?? tpl.Get(e.From!)), $"{e.Name} does not hold what was planned");
        foreach (var e in entries.Where(e => e.Name.EndsWith(".erp", StringComparison.OrdinalIgnoreCase) && e.Data is not null))
            Check(TryLoad(back.Get(e.Name)), $"{e.Name} cannot be read back as an ERP");
        if (!toProject) Check(back.Comment == tpl.Comment, "the SERPs injection target (zip comment) changed");
        else
        {
            var proj = SimplifiedProject.Read(path);
            foreach (var on in plan.SwitchOn) Check(proj.IsOn(on), $"{on} is not switched on");
            foreach (var a in plan.Added.Where(a => !a.Name.EndsWith('/') && !plan.SwitchOn.Contains(a.Name)))
                Check(!proj.IsOn(a.Name), $"{a.Name} is switched on although its group has another option on");
            if (source is { Legacy: false })
                foreach (var n in tpl.EntryNames.Where(n => !n.EndsWith('/') && !plan.Removed.Contains(n)))
                {
                    var o = plan.Renamed.GetValueOrDefault(n, n);
                    Check(proj.IsOn(o) == (plan.SwitchOn.Contains(o) || source.IsOn(n) && !plan.SwitchOff.Contains(n)), $"{o} is not switched as planned");
                }
            if (newProject)
                foreach (var n in plan.Replaced.Keys.Where(k => SimplifiedProject.ProjectPath(k) is not null))
                    Check(proj.IsOn(SimplifiedProject.ProjectPath(plan.Renamed.GetValueOrDefault(n, n))!), $"{n} changed but is not switched on");
        }
        return checks;
    }

    static bool TryLoad(byte[] erp)
    {
        try { ErpX.Load(erp); return true; }
        catch (InvalidDataException) { return false; }
    }
}
