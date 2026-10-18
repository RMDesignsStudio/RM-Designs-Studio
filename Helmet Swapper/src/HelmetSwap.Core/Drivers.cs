using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp.Data;

namespace HelmetSwap.Core;

/// <summary>A team as the character files name it (folder slot, e.g. "forceindia") and as people know it.
/// <paramref name="Color"/> = the colour key when it isn't the slot (e.g. a special livery uses its team's colour);
/// <paramref name="Edition"/> = a special-livery helmet ("Monza"); <paramref name="Story"/> = a Braking Point team.</summary>
public sealed record TeamInfo(string Slot, string Name, string FileName, string Series = "F1", string? Color = null, string? Edition = null, bool Story = false)
{
    public string ColorKey => Color ?? Slot;
}

/// <summary>One driver (any season the game has) and their helmet files.</summary>
public sealed class Driver
{
    public required string Folder { get; init; }        // e.g. 2026-ferrari_charles_leclerc
    public required string Gender { get; init; }        // male / female (folder under drivers/)
    public required TeamInfo Team { get; init; }
    public int Year { get; init; } = Drivers.Season;    // 2026 / 2025 (can be changed); 2024 = helmets to give only
    /// <summary>The 2026 and 2025 drivers can be changed (2025: the regular grids, special liveries and story drivers).</summary>
    public bool Changeable => Year >= Drivers.FirstChangeableYear;
    /// <summary>This helmet is identical to one listed for a newer season or to the regular livery's: left out of the
    /// helmets to give (the driver can still be changed).</summary>
    public bool IsCopy { get; set; }
    public string Series => Team.Series;                // F1 / F2
    public string? Edition => Team.Edition;             // "Monza", "Japan" (special livery helmet)
    /// <summary>What tells this helmet apart from the same driver's other helmet that season: the special livery
    /// ("Japan"), or the team when they drove for two (2025 Lawson / Tsunoda: "Red Bull", "Racing Bulls").</summary>
    public string? Variant { get; set; }
    /// <summary>Name as used in file and template names (F1 and F2 drivers alike; a career design: "Career Helmet Teal").</summary>
    public string Label => IsCareerDesign ? "Career Helmet " + Name : Name;
    public required string Token { get; init; }         // charles_leclerc (file / resource prefix)
    public required string Name { get; init; }          // Charles Leclerc
    public required string HelmetErp { get; init; }     // full path of <token>_helmet.erp
    public string? Shell { get; set; }                  // helmet_bell_hp7, helmet_schuberth_sf2_pro, ...
    /// <summary>The driver's helmet ERP has a shell binding for this driver alone (helmet_&lt;shell&gt;.emb?context=&lt;folder&gt;),
    /// which a swap to another make needs to hide their own shell.</summary>
    public bool OwnShellBinding { get; set; }
    /// <summary>The rear spoiler on top of the helmet: "solid wing", "clear wing" or "no wing" (from the helmet parts model).</summary>
    public string? Spoiler { get; set; }
    /// <summary>The helmet has a visor strip (a "visor_attachments" surface): all but 2024 F2 Isack Hadjar's do.</summary>
    public bool HasStrip { get; set; } = true;
    /// <summary>Fingerprint of the helmet's look (shell, own helmet textures, helmet parts geometry), to drop copies.</summary>
    public string? Signature { get; set; }

    /// <summary>Resource folder prefix, e.g. eaid://character_package/drivers/male/2026-ferrari_charles_leclerc/
    /// (career helmets: eaid://livery_package/replica/replica_hamilton08a/, eaid://livery_package/tier01/tier01_livery52/).</summary>
    public string EaidRoot => Root ?? $"eaid://character_package/drivers/{Gender}/{Folder}/";
    public string? Root { get; init; }
    /// <summary>Where the helmet's own textures are: &lt;EaidRoot&gt;textures/ for drivers; career helmets keep theirs in
    /// eaid://livery_package_shared/&lt;group&gt;/&lt;folder&gt;/textures/.</summary>
    public string TextureRoot => TexRoot ?? EaidRoot + "textures/";
    public string? TexRoot { get; init; }
    /// <summary>The helmet parts model, when it isn't the driver's own (career designs all share the My Team one).</summary>
    public string? AttachmentsRoot { get; init; }
    /// <summary>A career helmet (Series "Career"): a replica, or one of the standard designs.</summary>
    public bool IsCareer => Series == "Career";
    /// <summary>A standard career design: shared Arai shell and parts model, colours picked in game (mask).</summary>
    public bool IsCareerDesign => Team.Slot == "design";
    /// <summary>A texture the helmet owns (its own folders), as opposed to a shared / generic one.</summary>
    public bool OwnsTexture(string id) => id.StartsWith(EaidRoot, StringComparison.Ordinal) || id.StartsWith(TextureRoot, StringComparison.Ordinal);

    /// <summary>"charles-leclerc" for file names.</summary>
    public string Slug => Token.Replace('_', '-');

    /// <summary>Helmet make, lowercase: "bell", "schuberth", "stilo", "arai".</summary>
    public string Make => MakeName.ToLowerInvariant();
    /// <summary>Helmet make as a name: "Bell", "Schuberth", "Stilo", "Arai".</summary>
    public string MakeName => ShellName.Split(" ")[0];

    /// <summary>First name and last initial: "Max V", "Andrea A", "Carlos S" (a Jr / Sr suffix is skipped).</summary>
    public string ShortName
    {
        get
        {
            var w = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x is not ("Jr" or "Sr" or "Ii" or "Iii")).ToArray();
            return w.Length < 2 || char.IsDigit(w[^1][0]) ? Name : $"{w[0]} {char.ToUpperInvariant(w[^1][0])}";
        }
    }

    /// <summary>Which helmet this is, beyond the name: "" for 2026, "2024", "Japan 2025".</summary>
    public string YearTag => IsCareer ? Variant ?? "" : Year == Drivers.Season && Variant is null ? "" : Variant is null ? $"{Year}" : $"{Variant} {Year}";

    public string ShellName => Shell switch
    {
        "helmet_bell_hp7" => "Bell HP7",
        "helmet_schuberth_sf2_pro" => "Schuberth SF2 Pro",
        "helmet_stilo_st6" => "Stilo ST6",
        "helmet_stilo_st5" => "Stilo ST5",
        "helmet_arai_gp_6_01" => "Arai GP-6",
        null => "unknown shell",
        var s => s.Replace("helmet_", "").Replace('_', ' '),
    };

    public override string ToString() => $"{Name} ({Team.Name}{(YearTag == "" ? "" : ", " + YearTag)})";
}

public static class Drivers
{
    public const int Season = 2026;
    public const int FirstChangeableYear = 2025;
    public static readonly int[] Years = [2026, 2025, 2024];

    /// <summary>Character-file team slots for the 2026 F1 grid.</summary>
    public static readonly TeamInfo[] Teams2026 =
    [
        new("forceindia", "Aston Martin", "astonmartin"),
        new("ferrari", "Ferrari", "ferrari"),
        new("cadillac", "Cadillac", "cadillac"),
        new("haas", "Haas", "haas"),
        new("renault", "Alpine", "alpine"),
        new("mclaren", "McLaren", "mclaren"),
        new("mercedes", "Mercedes", "mercedes"),
        new("redbull", "Red Bull", "redbull"),
        new("sauber", "Audi", "audi"),
        new("tororosso", "Racing Bulls", "racingbulls"),
        new("williams", "Williams", "williams"),
    ];

    /// <summary>Character-file team slots for the 2026 F2 grid (folders "2026-f2-&lt;slot&gt;_&lt;driver&gt;").</summary>
    public static readonly TeamInfo[] TeamsF2 =
    [
        new("aixracing", "AIX Racing", "f2-aixracing", "F2"),
        new("artgrandprix", "ART Grand Prix", "f2-artgrandprix", "F2"),
        new("camposracing", "Campos Racing", "f2-camposracing", "F2"),
        new("dams", "DAMS", "f2-dams", "F2"),
        new("hitech", "Hitech", "f2-hitech", "F2"),
        new("invictaracing", "Invicta Racing", "f2-invictaracing", "F2"),
        new("mpmotorsport", "MP Motorsport", "f2-mpmotorsport", "F2"),
        new("prema", "Prema Racing", "f2-prema", "F2"),
        new("rodin", "Rodin Motorsport", "f2-rodin", "F2"),
        new("tridentmotorsport", "Trident", "f2-tridentmotorsport", "F2"),
        new("vanamersfoort", "Van Amersfoort Racing", "f2-vanamersfoort", "F2"),
    ];

    /// <summary>Older seasons' team slots (helmets as donors only): the 2025 / 2024 grids, the 2025 special liveries
    /// and the Braking Point story teams. Names as the teams were called that year.</summary>
    static TeamInfo[] TeamsOf(int year)
    {
        if (year == Season) return [.. Teams2026, .. TeamsF2];
        TeamInfo F1(string slot, string name, string? color = null, string? edition = null, bool story = false) => new(slot, name, slot, "F1", color, edition, story);
        TeamInfo F2(string slot, string name, string color) => new(slot, name, "f2-" + slot, "F2", color);
        var f1 = new List<TeamInfo>
        {
            F1("forceindia", "Aston Martin"), F1("ferrari", "Ferrari"), F1("haas", "Haas"), F1("renault", "Alpine"),
            F1("mclaren", "McLaren"), F1("mercedes", "Mercedes"), F1("redbull", "Red Bull"),
            F1("sauber", "Kick Sauber", "kicksauber"), F1("tororosso", year == 2024 ? "RB" : "Racing Bulls"), F1("williams", "Williams"),
            F1("konnersport", "Konnersport", story: true), F1("zenith", "APXGP", story: true),
        };
        if (year == 2025)
            f1.AddRange(
            [
                F1("ferrarimonza", "Ferrari", "ferrari", "Monza"), F1("redbulljapan", "Red Bull", "redbull", "Japan"),
                F1("haasmontreal", "Haas", "haas", "Montreal"), F1("haastexas", "Haas", "haas", "Texas"),
                F1("mclarensilverstone", "McLaren", "mclaren", "Silverstone"), F1("sauberbarcelona", "Kick Sauber", "kicksauber", "Barcelona"),
                F1("saubermiami", "Kick Sauber", "kicksauber", "Miami"), F1("tororossomiami", "Racing Bulls", "tororosso", "Miami"),
                F1("tororossosilverstone", "Racing Bulls", "tororosso", "Silverstone"), F1("williamsaustin", "Williams", "williams", "Austin"),
                F1("williamsgulf", "Williams", "williams", "Gulf"), F1("williamsvegas", "Williams", "williams", "Las Vegas"),
            ]);
        var f2 = year == 2025
            ? TeamsF2.Select(t => t with { Color = t.Slot }).ToList()
            : [F2("artgp", "ART Grand Prix", "artgrandprix"), F2("camposracing", "Campos Racing", "camposracing"), F2("dams", "DAMS", "dams"),
               F2("hitech", "Hitech", "hitech"), F2("invictaracing", "Invicta Racing", "invictaracing"), F2("mpmotorsport", "MP Motorsport", "mpmotorsport"),
               F2("phmracing", "PHM Racing", "phmracing"), F2("premaracing", "Prema Racing", "prema"), F2("rodinmotorsport", "Rodin Motorsport", "rodin"),
               F2("trident", "Trident", "tridentmotorsport"), F2("vanamersfoort", "Van Amersfoort Racing", "vanamersfoort")];
        return [.. f1, .. f2];
    }

    static readonly Regex FolderRe = new(@"^(\d{4})-(f2-)?([a-z]+)_(.+)$", RegexOptions.Compiled);
    static readonly Regex ShellRe = new(@"kitpart_assets/generic/helmets/([a-z0-9_]+)/idf/", RegexOptions.Compiled);
    static readonly Regex HelmetTexture = new(@"_(helmet|helmet_wing|visor|visor_attachments)_[a-z]+\.tif$", RegexOptions.Compiled);

    /// <summary>
    /// The 2026 F1 and F2 drivers that have a helmet file; with <paramref name="olderHelmets"/>, also every 2025 and 2024
    /// driver (F1, F2, Braking Point story drivers, 2025 special liveries). A helmet identical to one for a newer season
    /// or to the regular livery's is marked <see cref="Driver.IsCopy"/> (most special liveries reuse the normal helmet):
    /// left out of the helmets to give, kept as a driver to change (2025), dropped (2024).
    /// </summary>
    public static List<Driver> Find(GameInstall game, bool readShells = true, bool olderHelmets = false, bool career = false)
    {
        // the names the game shows (career helmets, replicas, the APXGP drivers), from its language file
        var text = GameText.Load(game);
        var list = new List<Driver>();
        foreach (var gender in new[] { "male", "female" })
        {
            var dir = Path.Combine(game.DriversDir, gender);
            if (!Directory.Exists(dir)) continue;
            foreach (var d in Directory.GetDirectories(dir))
            {
                var folder = Path.GetFileName(d);
                var m = FolderRe.Match(folder);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out var year) || !Years.Contains(year)) continue;
                if (year != Season && !olderHelmets) continue;
                bool f2 = m.Groups[2].Success;
                var team = TeamsOf(year).FirstOrDefault(t => t.Slot == m.Groups[3].Value && (t.Series == "F2") == f2);
                if (team is null) continue;
                var token = m.Groups[4].Value;
                var helmet = Path.Combine(d, token + "_helmet.erp");
                if (!File.Exists(helmet)) continue;
                var drv = new Driver { Folder = folder, Gender = gender, Team = team, Year = year, Token = token, Name = GameName(text, token) ?? DisplayName(token), HelmetErp = helmet };
                if (readShells || year != Season)
                {
                    try { Inspect(drv); }
                    catch { if (year != Season) continue; }
                    if (year != Season && (drv.Shell is null || drv.Signature is null)) continue;
                }
                list.Add(drv);
            }
        }
        // a helmet identical to one for a newer season, or (same season) to the regular livery's, is a copy: drop it
        var kept = new List<Driver>();
        foreach (var drv in list.OrderByDescending(x => x.Year).ThenBy(x => x.Edition is null ? 0 : 1))
        {
            drv.IsCopy = drv.Year != Season && drv.Signature is not null && kept.Any(k => !k.IsCopy && k.Signature == drv.Signature);
            if (drv.IsCopy && !drv.Changeable) continue;
            kept.Add(drv);
        }
        foreach (var g in kept.Where(x => x.Edition is null).GroupBy(x => (x.Year, x.Series, x.Token)).Where(g => g.Count() > 1))
            foreach (var x in g) x.Variant = x.Team.Name;
        foreach (var x in kept.Where(x => x.Edition is not null)) x.Variant ??= x.Edition;
        if (career) { kept.AddRange(FindReplicas(game, text)); kept.AddRange(FindDesigns(game, text)); }
        var teams = Years.SelectMany(TeamsOf).ToList();
        return kept.OrderByDescending(x => x.Year).ThenBy(x => x.IsCareer ? x.IsCareerDesign ? 2 : 1 : 0).ThenBy(x => x.Team.Story ? 1 : 0)
                   .ThenBy(x => teams.FindIndex(t => t.Slot == x.Team.Slot && t.Series == x.Series))
                   .ThenBy(x => NaturalKey(x.Name), StringComparer.Ordinal).ThenBy(x => NaturalKey(x.Variant ?? ""), StringComparer.Ordinal).ToList();
    }

    /// <summary>A name with its numbers zero-padded, so "Career Design 6" sorts before "Career Design 16".</summary>
    static string NaturalKey(string s) => Regex.Replace(s, @"\d+", m => m.Value.PadLeft(6, '0'));

    /// <summary>Shell, own shell binding and the helmet's fingerprint, from the driver's helmet ERP.</summary>
    static void Inspect(Driver drv)
    {
        var res = ErpX.Load(drv.HelmetErp).Resources;
        var ids = res.Select(r => r.Identifier).ToList();
        drv.Shell = ids.Select(i => ShellRe.Match(i)).Where(x => x.Success).Select(x => x.Groups[1].Value).FirstOrDefault();
        drv.OwnShellBinding = drv.Shell is not null && ids.Contains($"eaid://character_package/kitpart_assets/generic/helmets/{drv.Shell}/idf/{drv.Shell}.emb?context={drv.Folder}");
        // fingerprint: shell + own helmet textures' pixels (by kind) + the helmet parts' vertex/index data
        var byId = res.ToDictionary(r => r.Identifier, StringComparer.Ordinal);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.UTF8.GetBytes(drv.Shell ?? ""));
        var texPrefix = drv.TextureRoot + drv.Token;
        foreach (var r in res.Where(r => r.ResourceType == "GfxSRVResource" && r.Identifier.StartsWith(texPrefix, StringComparison.Ordinal) && HelmetTexture.IsMatch(r.Identifier))
                             .OrderBy(r => r.Identifier[texPrefix.Length..], StringComparer.Ordinal))
        {
            var srv = new ErpGfxSRVResource(); srv.FromResource(r);
            sha.AppendData(Encoding.UTF8.GetBytes(r.Identifier[texPrefix.Length..]));
            if (byId.TryGetValue(srv.Fragment0.SurfaceResourceName, out var surf)) sha.AppendData(ErpX.Data(surf, surf.Fragments.Count > 1 ? 1 : 0));
        }
        // helmet parts: their structure (surfaces, vertex counts, triangles); vertex floats are left out, re-exports of
        // the same parts for a special livery differ in the last bits
        var att = HelmetSwapper.Attachments(drv);
        if (byId.TryGetValue(att + "lod=0&mesh", out var mesh))
        {
            var slots = HelmetSwapper.MeshSlots(ErpX.Data(mesh));
            sha.AppendData(Encoding.UTF8.GetBytes(string.Join("|", slots)));
            drv.HasStrip = slots.Contains("visor_attachments");
        }
        foreach (var r in res.Where(r => r.Identifier.StartsWith(att + "lod=all&", StringComparison.Ordinal)).OrderBy(r => r.Identifier, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(r.Identifier[att.Length..]));
            var head = ErpX.Data(r, 0);
            if (r.Identifier.Contains("&ib=", StringComparison.Ordinal)) sha.AppendData(ErpX.Data(r, r.Fragments.Count > 1 ? 1 : 0));
            else sha.AppendData(head);                     // vertex buffer header: type, stride, count, format
        }
        drv.Signature = Convert.ToHexString(sha.GetHashAndReset());
        try { drv.Spoiler = SpoilerOf(byId, att); } catch { drv.Spoiler = null; }
    }

    /// <summary>
    /// What sits on top of the helmet at the back, from the helmet parts mesh: triangles whose centre is high up
    /// (y &gt; 1.64) and from the crown back (z &lt; 0.03; the clear vents are further forward). A "wing" surface there =
    /// a solid wing; else clear parts spanning more than 5 cm front-to-back = a clear wing; else no wing.
    /// </summary>
    static string SpoilerOf(Dictionary<string, EgoEngineLibrary.Archive.Erp.ErpResource> byId, string att)
    {
        var mesh = MeshDesc.Parse(ErpX.Data(byId[att + "lod=0&mesh"]));
        var ib = ErpX.Data(byId[mesh.IndexBuffer], 1);
        int solid = 0, clear = 0; float zMin = float.MaxValue, zMax = float.MinValue;
        for (int s = 0; s < mesh.Records.Count; s++)
        {
            if (mesh.Slots[s] is not ("wing" or "helmet_transparent")) continue;
            var pos = ErpX.Data(byId[mesh.Streams[mesh.StreamsOf(s)[0]]], 1);
            var (off, count) = mesh.IndexRange(s);
            int baseVertex = BitConverter.ToInt32(mesh.Records[s], 20);
            for (int t = 0; t + 2 < count; t += 3)
            {
                float y = 0, z = 0;
                for (int k = 0; k < 3; k++)
                {
                    int v = BitConverter.ToUInt16(ib, (off + t + k) * 2) + baseVertex;
                    y += BitConverter.ToSingle(pos, v * 12 + 4); z += BitConverter.ToSingle(pos, v * 12 + 8);
                }
                y /= 3; z /= 3;
                if (y <= 1.64f || z >= 0.03f) continue;
                if (mesh.Slots[s] == "wing") solid++;
                else { clear++; zMin = Math.Min(zMin, z); zMax = Math.Max(zMax, z); }
            }
        }
        return solid > 50 ? "solid wing" : clear > 50 && zMax - zMin > 0.05f ? "clear wing" : "no wing";
    }

    /// <summary>charles_leclerc -> Charles Leclerc; andrea-kimi_antonelli -> Andrea Kimi Antonelli; carlos_sainz-jr -> Carlos Sainz Jr;
    /// the Braking Point Zenith placeholders zenith_driverfirst -> Zenith Driver 1.</summary>
    // ---------------------------------------------------------------- career helmets

    public static readonly TeamInfo Replica = new("replica", "Career replica", "career-replica", "Career");
    public static readonly TeamInfo Design = new("design", "Career design", "career-design", "Career");
    const string MyTeamParts = "eaid://character_package/team_livery/noyear-custom-myteam/idf/noyear-custom-myteam_helmet_attachments.idf?";

    static readonly Dictionary<string, string> ReplicaNames = new()
    {
        ["antonelli"] = "Andrea Kimi Antonelli", ["bearman"] = "Oliver Bearman", ["hamilton"] = "Lewis Hamilton", ["leclerc"] = "Charles Leclerc",
        ["ocon"] = "Esteban Ocon", ["russell"] = "George Russell", ["verstappen"] = "Max Verstappen",
    };

    /// <summary>The APXGP drivers of the F1 movie ("zenith" is the game's name for that team in its files): the game's
    /// names for them (lng_zenith_driver_N_fullname "Sonny HAYES"), else these.</summary>
    static readonly string[] ApxgpDrivers = ["Sonny Hayes", "Joshua Pearce", "Luca Cortez"];

    /// <summary>A driver's name the game gives that the folder doesn't: the APXGP drivers (zenith_driverfirst -> Sonny Hayes).</summary>
    static string? GameName(GameText? text, string token)
    {
        if (!token.StartsWith("zenith_driver", StringComparison.Ordinal)) return null;
        int n = token[13..] switch { "first" => 1, "second" => 2, "third" => 3, _ => 0 };
        if (n == 0) return null;
        var full = text?[$"lng_zenith_driver_{n}_fullname"];
        // "Sonny HAYES" -> "Sonny Hayes"
        return full is null ? ApxgpDrivers[n - 1]
            : string.Join(" ", full.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 1 && w.All(c => !char.IsLower(c)) ? w[0] + w[1..].ToLowerInvariant() : w));
    }

    /// <summary>The game's title for a replica, without the driver's name: "Lewis Hamilton (2008)" -> "2008",
    /// "Esteban Ocon - Japan GP Special Edition" -> "Japan GP Special Edition", "Miami GP Special Edition - Antonelli" ->
    /// "Miami GP Special Edition", "Max Verstappen - Orange Lion 2025" -> "Orange Lion 2025"; null when it says nothing more.</summary>
    static string? TitleVariant(string? title, string name)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var v = title.Replace(name, " ", StringComparison.OrdinalIgnoreCase);
        var surname = name.Split(' ')[^1];
        if (surname.Length > 2) v = Regex.Replace(v, $@"\b{Regex.Escape(surname)}\b", " ", RegexOptions.IgnoreCase);
        v = Regex.Replace(v, @"\bHelmet\b", " ", RegexOptions.IgnoreCase);
        v = Regex.Replace(v.Replace("(", " ").Replace(")", " "), @"\s+", " ").Trim(' ', '-', '–', ',', ':');
        return v.Length == 0 ? null : v;
    }
    static readonly Regex ReplicaRe = new(@"^replica_([a-z]+?)(\d{2})([a-z]?)$", RegexOptions.Compiled);

    /// <summary>
    /// The career replica helmets (livery_package/replica/replica_&lt;driver&gt;&lt;yy&gt;&lt;a|b&gt;: Hamilton 2008, Verstappen 2025 B,
    /// F1 75...): built like a driver's helmet (full-colour design, own helmet parts model, own material and shell
    /// bindings), so any helmet can go onto them and from them. Shown as "Lewis Hamilton · 2008 Replica".
    /// </summary>
    static IEnumerable<Driver> FindReplicas(GameInstall game, GameText? text)
    {
        var dir = Path.Combine(game.Assets, "livery_package", "replica");
        if (!Directory.Exists(dir)) yield break;
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            var folder = Path.GetFileName(d);
            var helmet = Path.Combine(d, folder + "_helmet.erp");
            if (!File.Exists(helmet)) continue;
            var m = ReplicaRe.Match(folder);
            string name; string? variant;
            // the game's title for it ("Lewis Hamilton (2008)", "Esteban Ocon - Japan GP Special Edition", "F1® 75")
            var title = m.Success ? text?.First($"lng_helmet_{folder}", $"lng_{folder}", $"lng_replica_{m.Groups[1].Value}_{m.Groups[2].Value}{m.Groups[3].Value}", $"lng_livery_{folder}")
                                  : text?.First($"lng_helmet_{folder}", $"lng_{folder}", $"lng_livery_{folder}");
            string fallback = m.Success ? $"20{m.Groups[2].Value} Replica" + (m.Groups[3].Value is "b" or "c" ? " " + m.Groups[3].Value.ToUpperInvariant() : "") : "Replica";
            if (m.Success && m.Groups[1].Value == "zenith")
            {
                // the APXGP replicas: Sonny Hayes's (a) and Joshua Pearce's (b) helmets
                name = GameName(text, m.Groups[3].Value == "b" ? "zenith_driversecond" : "zenith_driverfirst")!;
                variant = $"APXGP 20{m.Groups[2].Value}";
            }
            else if (m.Success && ReplicaNames.TryGetValue(m.Groups[1].Value, out var full))
            {
                name = full;
                variant = TitleVariant(title, full) ?? fallback;
            }
            else if (folder == "replica_f175") { name = title ?? "F1 75"; variant = null; }
            else { name = title ?? DisplayName(folder["replica_".Length..]); variant = title is null ? "Replica" : null; }
            var drv = new Driver
            {
                Folder = folder, Gender = "male", Team = Replica, Year = Season, Token = folder, Name = name, HelmetErp = helmet,
                Root = $"eaid://livery_package/replica/{folder}/", TexRoot = $"eaid://livery_package_shared/replica/{folder}/textures/",
            };
            drv.Variant = variant;
            try { Inspect(drv); } catch { continue; }
            if (drv.Shell is null) continue;
            yield return drv;
        }
    }

    static readonly Regex DesignRe = new(@"^(tier0(\d)|novelty|unassigned)_([a-z]+?)(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// The standard career helmet designs (livery_package/tier01..05, novelty, unassigned): their colours are picked in
    /// game (a mask), they all wear the Arai GP-6 shell and share one helmet parts model (My Team's), so a design (from
    /// an Arai helmet) and a visor can go onto them, not the wing or attachments. Shown as "Career Design 52 · Tier 1".
    /// </summary>
    static IEnumerable<Driver> FindDesigns(GameInstall game, GameText? text)
    {
        var root = Path.Combine(game.Assets, "livery_package");
        if (!Directory.Exists(root)) yield break;
        foreach (var group in new[] { "tier01", "tier02", "tier03", "tier04", "tier05", "novelty", "unassigned" })
        {
            var dir = Path.Combine(root, group);
            if (!Directory.Exists(dir)) continue;
            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => DesignRe.Match(Path.GetFileName(x)) is { Success: true } mm ? int.Parse(mm.Groups[4].Value) : 0))
            {
                var folder = Path.GetFileName(d);
                var helmet = Path.Combine(d, folder + "_helmet.erp");
                var m = DesignRe.Match(folder);
                if (!File.Exists(helmet) || !m.Success) continue;
                string number = m.Groups[4].Value.TrimStart('0');
                // the name the game shows for it ("Teal", "Heatsink"; Novelty Hamilton 2 is "Roscoe")
                var title = text?.First($"lng_{folder}", $"lng_helmet_{folder}", $"lng_livery_{folder}");
                var (name, variant) = group switch
                {
                    "novelty" => (title ?? $"Novelty {DisplayName(m.Groups[3].Value)} {number}", "Career"),
                    "unassigned" => (title ?? $"Career Design {number}", "Unassigned"),
                    _ => (title ?? $"Career Design {number}", $"Tier {m.Groups[2].Value}"),
                };
                // some carry their own helmet parts model (those can take a wing / attachments too); Novelty Hamilton 2 is a
                // full-colour helmet built like a replica, listed with the replicas
                var rootId = $"eaid://livery_package/{group}/{folder}/";
                bool ownParts;
                try { ownParts = ErpX.Load(helmet).Resources.Any(r => r.Identifier == $"{rootId}idf/{folder}_helmet_attachments.idf?model"); } catch { continue; }
                bool novelty = ownParts && group == "novelty";
                var drv = new Driver
                {
                    Folder = folder, Gender = "male", Team = novelty ? Replica : Design, Year = Season, Token = folder,
                    Name = novelty ? ReplicaNames.GetValueOrDefault(m.Groups[3].Value, name) : name, HelmetErp = helmet,      // novelty: "Lewis Hamilton"
                    Root = rootId, TexRoot = $"eaid://livery_package_shared/{group}/{folder}/textures/",
                    AttachmentsRoot = ownParts ? null : MyTeamParts,
                };
                drv.Variant = novelty ? title ?? $"Novelty {number}" : variant;          // "Roscoe"
                try { Inspect(drv); } catch { continue; }
                if (drv.Shell is null) continue;
                yield return drv;
            }
        }
    }

    public static string DisplayName(string token)
    {
        if (token.StartsWith("zenith_driver", StringComparison.Ordinal)) return GameName(null, token) ?? token;
        var ti = CultureInfo.InvariantCulture.TextInfo;
        return string.Join(" ", token.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(w => ti.ToTitleCase(w)));
    }
}
