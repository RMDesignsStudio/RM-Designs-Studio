using System.Xml;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Xml;

namespace Swapper.Core;

/// <summary>What the swapped car's height setting did, for the log and the checks.</summary>
public sealed class CarLookResult
{
    /// <summary>The handling files carried with the new values (the team's and its FOM-mode one).</summary>
    public List<string> Files { get; } = [];
    /// <summary>The values set, by key (e.g. "Chassis/FrontVisualHeightOffset" -> "0.032").</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Where the game draws a car: every car's handling file (<c>teams/T/handling/T.vtf</c>, and <c>fom_car_T.vtf</c> for the
/// equal-cars mode) has a few values that only place the drawn car, tuned to its own model:
/// <c>Chassis/FrontVisualHeightOffset</c> and <c>RearVisualHeightOffset</c> (the body) and each axle's
/// <c>Suspension/LocalRenderPositionY</c> (the wheels). They run from -1 mm (Cadillac) to +37 mm (Red Bull), and the Racing
/// Bulls' rear axle is -42 mm.
///
/// A chassis swap keeps the team's handling (performance, physics and collision shape are the team's), so the donor car is
/// drawn with the team's values: an Aston Martin in the Cadillac slot sits 33 mm too low, and so does everything placed
/// against it, such as the garage monitors over its nose. This step carries the team's handling files in the swapped
/// option's ERP with only those four values set to the donor's. Everything else stays the team's, byte for byte.
///
/// The files are binary XML (the first fragment, which EgoEngineLibrary reads and writes back byte for byte) followed by a
/// "TSMD" table of its sizes (the second fragment): the numbers of elements and attributes, the lengths of their names
/// and values, and the numbers and lengths of the distinct values and strings. Those are recomputed. The table is checked
/// against the game's own file first, so a file laid out any other way is left alone.
/// </summary>
public static class CarLook
{
    /// <summary>The values that place the drawn car (element path, and the suspension name for an axle).</summary>
    static readonly (string Key, string XPath)[] Keys =
    [
        ("Chassis/FrontVisualHeightOffset", "//Chassis/FrontVisualHeightOffset"),
        ("Chassis/RearVisualHeightOffset", "//Chassis/RearVisualHeightOffset"),
        ("Suspension Front/LocalRenderPositionY", "//Suspension[@name='Front']/LocalRenderPositionY"),
        ("Suspension Rear/LocalRenderPositionY", "//Suspension[@name='Rear']/LocalRenderPositionY"),
    ];

    /// <summary>Give the swapped chassis option <paramref name="erpBytes"/> (in <paramref name="teamFolder"/>'s slot) the
    /// donor's drawn height. Returns the ERP unchanged when both cars are drawn alike.</summary>
    /// <param name="lift">Test builds only: draw the car this many metres higher than the donor is (to see what follows it).</param>
    public static (byte[] Erp, CarLookResult Result) Apply(GameData game, byte[] erpBytes, ChassisDonor donor, string teamFolder, SwapLog log, float lift = 0)
    {
        var result = new CarLookResult();
        var team = HandlingOf(game, teamFolder, donor.Season);
        var donorErp = ErpX.Load(donor.ErpPath);
        var erp = ErpX.Load(erpBytes);
        foreach (var tv in team.Resources.Where(IsHandling))
        {
            // the donor's file of the same kind (its own car's, or its equal-cars one)
            var dv = donorErp.Resources.FirstOrDefault(r => IsHandling(r) && IsFom(r) == IsFom(tv));
            if (dv is null) { log.Warn($"Height: {donor.Label} has no {(IsFom(tv) ? "equal-cars " : "")}handling file; the car is drawn with {teamFolder}'s."); continue; }
            var want = Values(Xml(dv));
            if (lift != 0)
                foreach (var k in want.Keys.ToList())
                    want[k] = (float.Parse(want[k], System.Globalization.CultureInfo.InvariantCulture) + lift).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var doc = Xml(tv);
            if (Values(doc).SequenceEqual(want)) continue;
            var table = ErpX.Data(tv, 1);
            if (!Table(doc).SequenceEqual(Known(table)))
            {
                log.Warn($"Height: {tv.Identifier.Split('/')[^1]} is laid out in a way this app doesn't know; the car is drawn with {teamFolder}'s height.");
                continue;
            }
            foreach (var (key, path) in Keys)
                ((XmlElement)doc.Document.SelectSingleNode(path)!).SetAttribute("value", want[key]);
            using var o = new MemoryStream();
            doc.Write(o, doc.Type);
            var bxml = o.ToArray();
            var newTable = WithTable(table, Table(Xml(bxml)));
            tv.Fragments[0].SetData(bxml, true);
            tv.Fragments[1].SetData(newTable, true);
            ErpX.Rehash(tv);
            ErpX.AddOrReplace(erp, tv);
            result.Files.Add(tv.Identifier);
            foreach (var (k, v) in want) result.Values[k] = v;
        }
        if (result.Files.Count == 0) return (erpBytes, result);
        log.Info($"Height: the car is drawn as {donor.Label} is (body {Mm(result.Values[Keys[0].Key])}, front wheels {Mm(result.Values[Keys[2].Key])}, " +
                 $"rear wheels {Mm(result.Values[Keys[3].Key])}); {teamFolder}'s handling is otherwise kept as it is ({result.Files.Count} file(s)).");
        return (ErpX.Save(erp), result);
    }

    /// <summary>The finished ERP read back: each carried handling file has the donor's values, the team's for everything
    /// else, and a size table that matches it. Returns the number of checks.</summary>
    public static int Verify(GameData game, byte[] erpBytes, ChassisDonor donor, string teamFolder, CarLookResult result)
    {
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) throw new InvalidDataException($"Height check failed: {what}"); }
        if (result.Files.Count == 0) return 0;
        var erp = ErpX.Load(erpBytes);
        var team = HandlingOf(game, teamFolder, donor.Season);
        foreach (var id in result.Files)
        {
            var r = erp.Resources.FirstOrDefault(x => x.Identifier == id);
            Check(r is not null, $"{id.Split('/')[^1]} is not in the chassis");
            var doc = Xml(r!);
            var vals = Values(doc);
            Check(Keys.All(k => vals[k.Key] == result.Values[k.Key]), $"{id.Split('/')[^1]} doesn't place the car as {donor.Label}");
            Check(Table(doc).SequenceEqual(Known(ErpX.Data(r!, 1))), $"{id.Split('/')[^1]}'s size table doesn't match it");
            // everything but the four values is the team's
            var mine = Xml(team.Resources.First(x => x.Identifier == id));
            foreach (var (key, path) in Keys) ((XmlElement)mine.Document.SelectSingleNode(path)!).SetAttribute("value", vals[key]);
            Check(mine.Document.OuterXml == doc.Document.OuterXml, $"{id.Split('/')[^1]} differs from the team's in more than the car's height");
        }
        return checks;
    }

    /// <summary>The ERP holding a slot's handling files: its car file, or (the FOM car, whose car file has none) the
    /// teams' shared <c>common.erp</c>, narrowed to that slot's.</summary>
    static ErpFile HandlingOf(GameData game, string teamFolder, int season)
    {
        var team = ErpX.Load(TeamSimplified.ChassisSwap.TeamErpPath(game, teamFolder, season));
        if (team.Resources.Any(IsHandling)) return team;
        var common = Path.Combine(game.Assets, $"f1_{season}_vehicle_package", "teams", "common.erp");
        if (!File.Exists(common)) return team;
        var shared = ErpX.Load(common);
        shared.Resources = shared.Resources.Where(r => IsHandling(r) && r.Identifier.Contains($"/teams/{teamFolder}/handling/", StringComparison.Ordinal)).ToList();
        return shared.Resources.Count > 0 ? shared : team;
    }

    static bool IsHandling(ErpResource r) => r.Identifier.EndsWith(".vtf", StringComparison.Ordinal) && r.Fragments.Count == 2;
    static bool IsFom(ErpResource r) => r.Identifier.Split('/')[^1].StartsWith("fom_car_", StringComparison.Ordinal);
    static XmlFile Xml(ErpResource r) => Xml(ErpX.Data(r, 0));
    static XmlFile Xml(byte[] d) => new(new MemoryStream(d));

    static Dictionary<string, string> Values(XmlFile x) =>
        Keys.ToDictionary(k => k.Key, k => (x.Document.SelectSingleNode(k.XPath) as XmlElement)?.GetAttribute("value")
                                           ?? throw new InvalidDataException($"the handling file has no {k.Key}"), StringComparer.Ordinal);

    static string Mm(string v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
        ? $"{d * 1000:+0;-0;0} mm" : v;

    // ---- the TSMD size table: "TSMD", then 29 little-endian numbers (index = field number - 1)
    // fields 2/4/12 attributes, 3 their name lengths, 5/13 value lengths, 6 elements, 7 their name lengths, 10 elements +
    // attributes, 11 all name lengths, 14 elements + 2 x attributes, 15 all lengths, 18/26 distinct values, 19/27 their
    // lengths, 28 distinct strings (names and values), 29 their lengths; the others are constants of the format or depend on
    // the names only, which this never changes
    static readonly int[] Fields = [2, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15, 18, 19, 26, 27, 28, 29];

    static uint[] Table(XmlFile x)
    {
        var els = x.Document.DocumentElement!.SelectNodes("descendant-or-self::*")!.Cast<XmlElement>().ToList();
        var attrs = els.SelectMany(e => e.Attributes.Cast<XmlAttribute>()).ToList();
        var vals = attrs.Select(a => a.Value).ToList();
        var strings = els.Select(e => e.Name).Concat(attrs.Select(a => a.Name)).Concat(vals).Distinct(StringComparer.Ordinal).ToList();
        uint e = (uint)els.Count, a = (uint)attrs.Count;
        uint en = (uint)els.Sum(x => x.Name.Length), an = (uint)attrs.Sum(x => x.Name.Length), vl = (uint)vals.Sum(v => v.Length);
        var dv = vals.Distinct(StringComparer.Ordinal).ToList();
        uint dvc = (uint)dv.Count, dvl = (uint)dv.Sum(v => v.Length), dsc = (uint)strings.Count, dsl = (uint)strings.Sum(s => s.Length);
        return [a, an, a, vl, e, en, e + a, en + an, a, vl, e + 2 * a, en + an + vl, dvc, dvl, dvc, dvl, dsc, dsl];
    }

    static uint[] Known(byte[] table)
    {
        if (table.Length < 120 || table[0] != (byte)'T' || table[1] != (byte)'S' || table[2] != (byte)'M' || table[3] != (byte)'D') return [];
        return Fields.Select(f => BitConverter.ToUInt32(table, 4 * f)).ToArray();
    }

    static byte[] WithTable(byte[] table, uint[] values)
    {
        var t = (byte[])table.Clone();
        for (int i = 0; i < Fields.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(t, 4 * Fields[i]);
        return t;
    }
}
