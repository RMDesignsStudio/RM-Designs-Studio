// Ported from Simplified Suite by Team Simplified (GPL-3.0): EGO Engine Library/Archive/Erp/CarErp.cs
// https://github.com/TeamSimplified/simplified-suite - see NOTICE-Simplified-Suite.md
// Changes: reads archives with the Studio's ErpX; answers are remembered per file (path, size, write time).

using EgoEngineLibrary.Archive.Erp;

namespace Swapper.Core.TeamSimplified;

/// <summary>
/// What tells a car ERP apart from something that merely lives beside one. A team's <c>wep/</c> folder holds the
/// team's car and its special liveries (<c>toro_rosso.erp</c> next to <c>toro_rosso_suzuka_26.erp</c>). Most
/// liveries are whole cars with their own <c>/wep/</c> setup; some are texture overlays with none, and those are
/// neither a car to swap in nor a slot to swap into.
/// </summary>
public static class CarErps
{
    public const string WepSegment = "/wep/";

    /// <summary>True when the archive carries the <c>/wep/</c> setup that makes the game load it as a car.</summary>
    public static bool IsCar(ErpFile erp) =>
        erp.Resources.Any(r => r.Identifier.Contains(WepSegment, StringComparison.OrdinalIgnoreCase));

    static readonly Dictionary<(string, long, DateTime), bool> Memo = [];

    /// <summary>The same question about a file on disk (false for a file that can't be read).</summary>
    public static bool IsCar(string path)
    {
        (string, long, DateTime) key;
        try { var fi = new FileInfo(path); key = (path, fi.Length, fi.LastWriteTimeUtc); }
        catch (Exception) { return false; }
        lock (Memo) if (Memo.TryGetValue(key, out var known)) return known;
        bool answer;
        try { answer = IsCar(ErpX.Load(path)); }
        catch (Exception) { answer = false; }
        lock (Memo) Memo[key] = answer;
        return answer;
    }

    /// <summary>Every car ERP in a team's <c>wep/</c> folder: the team's own first, then its special liveries by name.</summary>
    public static List<string> InFolder(string wepDir, string team)
    {
        if (!Directory.Exists(wepDir)) return [];
        List<string> files;
        try { files = Directory.EnumerateFiles(wepDir, "*.erp").ToList(); }
        catch (Exception) { return []; }
        return files.Where(IsCar)
                    .OrderByDescending(p => IsBaseCar(p, team))
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>The team a car ERP belongs to, from the folder it sits in (<c>teams/&lt;team&gt;/wep/x.erp</c>).</summary>
    public static string? TeamOf(string erpPath)
    {
        var teamDir = Path.GetDirectoryName(Path.GetDirectoryName(erpPath));
        var team = teamDir is null ? null : Path.GetFileName(teamDir);
        return string.IsNullOrEmpty(team) ? null : team;
    }

    /// <summary>True for <c>&lt;team&gt;/wep/&lt;team&gt;.erp</c>, the car the team actually races.</summary>
    public static bool IsBaseCar(string erpPath, string team) =>
        string.Equals(Path.GetFileNameWithoutExtension(erpPath), team, StringComparison.OrdinalIgnoreCase);

    /// <summary>A special livery's name from its file: <c>toro_rosso_suzuka_26</c> -> "Suzuka 26" (null for the base
    /// car). The trailing number is the livery's own year, not the season's.</summary>
    public static string? LiveryName(string erpPath, string team)
    {
        if (IsBaseCar(erpPath, team)) return null;
        var name = Path.GetFileNameWithoutExtension(erpPath);
        var suffix = name.StartsWith(team + "_", StringComparison.OrdinalIgnoreCase) ? name[(team.Length + 1)..] : name;
        return string.Join(' ', suffix.Split('_', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(w => char.IsLetter(w[0]) ? char.ToUpperInvariant(w[0]) + w[1..] : w));
    }
}
