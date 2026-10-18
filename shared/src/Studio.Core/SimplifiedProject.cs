using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Studio.Core;

/// <summary>
/// Simplified Studio projects (.simplified): a zip with <c>info.json</c> (name, author, version, shader package, the
/// template it came from, and the parts switched on) and the template's ERPs under <c>ERPs/</c>, in the same folders as
/// in a .serps. Projects from before Studio 1.3 have <c>mod.json</c> and <c>default/</c>, <c>enabled/</c>,
/// <c>disabled/</c> folders instead. Both open like a template (the ERP layout is what the chassis code reads); a
/// current project is written back in its own format.
/// </summary>
public sealed class SimplifiedProject
{
    public const string Extension = ".simplified";
    const string Info = "info.json", LegacyInfo = "mod.json", Erps = "ERPs/";

    public string Name { get; private init; } = "";
    public string Author { get; private init; } = "";
    public string Version { get; private init; } = "";
    /// <summary>The Studio build that wrote it ("2.0"), when it says.</summary>
    public string StudioVersion { get; private init; } = "";
    /// <summary>The .serps the project was made from (file name), or "" for a custom project.</summary>
    public string SerpsTemplate { get; private init; } = "";
    /// <summary>The shader package the project is packaged with (info.json; a template keeps it in its zip comment), or "".</summary>
    public string ShaderPackageName { get; private init; } = "";

    /// <summary>
    /// A SERPs template in <paramref name="dirs"/> (and their sub-folders, one deep) that carries a chassis like
    /// <paramref name="entry"/>: the same file in the same folder, or the same file name. For a project made from a
    /// custom template, which names none. Templates directly in a folder come before those in its sub-folders.
    /// </summary>
    public static string? TemplateWith(CarEntry entry, IEnumerable<string?> dirs)
    {
        var name = entry.ZipPath[(entry.ZipPath.LastIndexOf('/') + 1)..];
        string? byName = null;
        foreach (var d in dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(d!, "*.serps", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                                 .OrderBy(f => string.Equals(Path.GetDirectoryName(f), Path.TrimEndingDirectorySeparator(d!), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                 .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception) { continue; }
            foreach (var f in files)
            {
                try
                {
                    using var z = ZipFile.OpenRead(f);
                    if (z.Entries.Any(e => e.FullName == entry.ZipPath)) return f;
                    if (byName is null && z.Entries.Any(e => e.Name == name)) byName = f;
                }
                catch (Exception) { }
            }
        }
        return byName;
    }

    /// <summary>Pre-1.3 layout (default / enabled / disabled folders).</summary>
    public bool Legacy { get; private init; }
    /// <summary>The parts switched on, as zip paths ("ERPs/02--01--.../x.erp").</summary>
    public HashSet<string> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsProject(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Make sure a .simplified is a project: Simplified Studio also keeps its settings in a .simplified
    /// (team.simplified, a small JSON file), which is not one.</summary>
    public static void Check(string path)
    {
        Span<byte> head = stackalloc byte[4];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.Read(head);
        if (n >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K') return;   // a zip
        int i = 0;
        while (i < n && head[i] is 32 or 13 or 10 or 9 or 0xEF or 0xBB or 0xBF) i++;   // spaces, line breaks, tabs, a BOM
        if (i < n && head[i] == (byte)'{')
            throw new InvalidDataException($"{Path.GetFileName(path)} is a Simplified Studio settings file, not a project. Open a project (.simplified made with Save in Simplified Studio) or a .serps template.");
        throw new InvalidDataException($"{Path.GetFileName(path)} is not a Simplified Studio project (not a zip file); it may be damaged or incomplete.");
    }

    /// <summary>Read a project's description (the ERPs are read by <see cref="SerpsTemplate"/>).</summary>
    public static SimplifiedProject Read(string path)
    {
        Check(path);
        using var zip = ZipFile.OpenRead(path);
        var info = zip.GetEntry(Info) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(Info, StringComparison.OrdinalIgnoreCase));
        if (info is null)
        {
            // pre-1.3: mod.json; what is switched on is the enabled/ folder (default/ holds the base parts)
            var legacy = new SimplifiedProject { Legacy = true };
            var mod = zip.GetEntry(LegacyInfo);
            if (mod is not null)
            {
                var j = Parse(mod);
                legacy = new SimplifiedProject { Legacy = true, Name = Str(j, "name"), Author = Str(j, "author"), Version = Str(j, "version") };
            }
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("enabled/", StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0))
                legacy.Enabled.Add(e.FullName);
            return legacy;
        }
        var root = Parse(info);
        string studio = root?["simplified_app"] is JsonArray app && app.Count > 1 ? app[1]?.GetValue<string>() ?? "" : Str(root, "studio_version");
        var p = new SimplifiedProject { Name = Str(root, "name"), Author = Str(root, "author"), Version = Str(root, "version"), StudioVersion = studio.TrimStart('v', 'V'),
                                        SerpsTemplate = Str(root, "serps_template"), ShaderPackageName = Str(root, "shader_package") };
        if (root?["enabled"] is JsonArray on)
            foreach (var x in on)
                if (x?.GetValue<string>() is { Length: > 0 } rel) p.Enabled.Add(Erps + rel.Replace('\\', '/'));
        return p;
    }

    static JsonNode? Parse(ZipArchiveEntry e)
    {
        try
        {
            using var s = e.Open();
            return JsonNode.Parse(s, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException) { return null; }
    }

    static string Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>Whether a part is switched on in this project.</summary>
    public bool IsOn(string zipPath) => Legacy
        ? !zipPath.StartsWith("disabled/", StringComparison.OrdinalIgnoreCase)
        : Enabled.Contains(zipPath);

    /// <summary>
    /// The chassis a project should open on: the first one switched on. In the older layout a part in enabled/ (the
    /// one the author switched on) comes before the default/ copies; -1 when none is on.
    /// </summary>
    public int Preferred(IReadOnlyList<string> zipPaths)
    {
        if (Legacy)
        {
            var en = zipPaths.ToList().FindIndex(z => LegacyFolder(z) == "enabled");
            if (en >= 0) return en;
        }
        return zipPaths.ToList().FindIndex(IsOn);
    }

    /// <summary>A pre-1.3 part's folder ("default", "enabled", "disabled"), or null.</summary>
    public static string? LegacyFolder(string zipPath)
    {
        var f = zipPath.Contains('/') ? zipPath[..zipPath.IndexOf('/')] : "";
        return f is "default" or "enabled" or "disabled" ? f : null;
    }

    /// <summary>Where a template's file goes in a project made from it (as Studio does): ERPs/... stays where it is; a flat
    /// (pre-1.3, e.g. the F1 25 2025-car) template's top-level ERP goes straight under ERPs/; anything else is left out.</summary>
    public static string? ProjectPath(string entry) =>
        entry.StartsWith(Erps, StringComparison.Ordinal) ? (entry.Length > Erps.Length ? entry : null)
        : !entry.Contains('/') && entry.EndsWith(".erp", StringComparison.OrdinalIgnoreCase) ? Erps + entry : null;

    /// <summary>
    /// A new project from a .serps template, as Simplified Studio makes one: <c>info.json</c> plus the template's ERPs
    /// (and its folders) under <c>ERPs/</c>, with <paramref name="replaced"/> swapped in and those parts switched on.
    /// <paramref name="renamed"/> moves parts and folders to new paths (zip path -> new zip path), e.g. an option folder
    /// named after the wheel it got. The writer is recorded in a field of its own (<c>rm_app</c>: <paramref name="appId"/>
    /// and its version); <c>simplified_app</c> is left to Studio.
    /// </summary>
    public static void WriteFromTemplate(SerpsTemplate tpl, string outPath, IReadOnlyDictionary<string, byte[]> replaced, string name,
                                         string appId, string appVersion, IReadOnlyDictionary<string, string>? renamed = null)
    {
        string Out(string n) => ProjectPath(renamed?.GetValueOrDefault(n, n) ?? n) ?? ProjectPath(n)!;
        using var fs = File.Create(outPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var ie = zip.CreateEntry(Info, CompressionLevel.Optimal);
        using (var s = ie.Open()) s.Write(NewInfo(tpl, name, appId, appVersion, replaced.Keys.Where(k => ProjectPath(k) is not null).Select(Out)));
        foreach (var n in tpl.EntryNames)
        {
            if (ProjectPath(n) is null) continue;
            var p = Out(n);
            if (n.EndsWith('/')) { zip.CreateEntry(p); continue; }
            var e = zip.CreateEntry(p, CompressionLevel.Fastest);   // ERP data is already compressed inside
            using var s = e.Open();
            s.Write(replaced.GetValueOrDefault(n) ?? tpl.Get(n));
        }
    }

    /// <summary>
    /// The <c>info.json</c> of a new project made from a template, as Studio writes one, with <paramref name="enabled"/>
    /// (project zip paths, "ERPs/...") switched on. The writer is recorded in <c>rm_app</c>.
    /// </summary>
    public static byte[] NewInfo(SerpsTemplate tpl, string name, string appId, string appVersion, IEnumerable<string> enabled)
    {
        var info = new JsonObject
        {
            ["name"] = name,
            ["author"] = "",
            ["version"] = TemplateVersion(tpl),
            ["shader_package"] = ShaderPackage(tpl.Comment),
            ["serps_template"] = Path.GetFileName(tpl.SourcePath),
            ["custom_template"] = false,
            ["enabled"] = new JsonArray(enabled.Where(k => k.StartsWith(Erps, StringComparison.Ordinal)).Order()
                                               .Select(k => (JsonNode)JsonValue.Create(k[Erps.Length..].Replace('/', '\\'))!).ToArray()),
            ["rm_app"] = new JsonArray(appId, "v" + appVersion),
        };
        return System.Text.Encoding.UTF8.GetBytes(info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// A project's <c>info.json</c> with its switched-on parts moved to their new paths (<paramref name="renamed"/>: zip
    /// path -> new zip path), <paramref name="switchOff"/> switched off and <paramref name="switchOn"/> switched on (zip
    /// paths after renaming); null when nothing changes, so the file is kept byte for byte. Every other field stays as
    /// Studio wrote it.
    /// </summary>
    public static byte[]? UpdatedInfo(byte[] infoJson, IReadOnlyDictionary<string, string> renamed, IEnumerable<string>? switchOn = null,
                                      IEnumerable<string>? switchOff = null)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(infoJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject; }
        catch (JsonException) { return null; }
        if (root is null) return null;
        static string Zip(string rel) => Erps + rel.Replace('\\', '/');
        static string Rel(string zip) => zip[Erps.Length..].Replace('/', '\\');
        var on = (root["enabled"] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).OfType<string>().ToList() ?? [];
        var next = on.Select(r => renamed.TryGetValue(Zip(r), out var to) ? Rel(to) : r).ToList();
        var off = (switchOff ?? []).Where(z => z.StartsWith(Erps, StringComparison.Ordinal)).Select(Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        next.RemoveAll(off.Contains);
        foreach (var z in switchOn ?? [])
            if (z.StartsWith(Erps, StringComparison.Ordinal) && !next.Contains(Rel(z), StringComparer.OrdinalIgnoreCase)) next.Add(Rel(z));
        if (next.SequenceEqual(on)) return null;
        root["enabled"] = new JsonArray(next.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The shader package a template names in its zip comment ({"erp_file_string": "..."}).</summary>
    public static string ShaderPackage(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return "";
        try { return JsonNode.Parse(comment)?["erp_file_string"]?.GetValue<string>() ?? ""; }
        catch (Exception) { return ""; }
    }

    static string TemplateVersion(SerpsTemplate tpl)
    {
        var entry = tpl.EntryNames.FirstOrDefault(n => n.Equals("template.serps", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return "";
        try { return JsonNode.Parse(tpl.Get(entry))?["version"]?.GetValue<string>() ?? ""; }
        catch (Exception) { return ""; }
    }
}
