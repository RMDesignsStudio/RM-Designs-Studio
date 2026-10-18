// Ported from Simplified Suite by Team Simplified (GPL-3.0): Simplified Swapper/src/SimplifiedSwapper.Core/Project/TeamErps.cs
// https://github.com/TeamSimplified/simplified-suite - see NOTICE-Simplified-Suite.md
// Changes: only the shader package table and its questions (also read from a .simplified project's info.json); the
// game's car files are listed by Catalog.

namespace Swapper.Core.TeamSimplified;

/// <summary>
/// Which team a template is for. Its shader package says so: every team in the 2026 pack has its own, and it
/// travels in the .serps zip comment as <c>{"erp_file_string":"object_flag.nefx2.sm51"}</c>. The pairing is fixed
/// (the shader names carry no meaning: Ferrari is <c>object_flag</c>, Williams <c>object_roof</c>), and the game's
/// team folders are historical: Alpine is <c>lotus</c>, Aston Martin <c>force_india</c>, Audi <c>sauber</c>,
/// Racing Bulls <c>toro_rosso</c>.
/// </summary>
public static class TemplateTeams
{
    /// <param name="Name">The team as people call it.</param>
    /// <param name="Folders">The team's folders under <c>teams/</c>: one each, except My Team (a slot per engine).</param>
    /// <param name="SuitSet">The driverwear set under <c>character_package/team_livery</c> its drivers wear; null for
    /// My Team, whose drivers wear a MyTeam suit.</param>
    /// <param name="ChassisSwapWorks">Whether a chassis can be swapped in (My Team's goes into the FOM car's slot, see
    /// <see cref="MyTeamChassis"/>).</param>
    /// <param name="SuitSwapWorks">Whether its drivers' suits can be swapped (My Team's are built differently, onto the
    /// template's own driverwear part: see <see cref="MyTeamSuitSwap"/>).</param>
    public sealed record Team(string Name, string[] Folders, string? SuitSet, bool ChassisSwapWorks = true, bool SuitSwapWorks = true)
    {
        /// <summary>My Team: a slot per engine supplier, the car spawned from the FOM car's slot.</summary>
        public bool IsMyTeam => Folders[0].StartsWith("myteam_", StringComparison.Ordinal);
    }

    static readonly Dictionary<string, Team> ByShaderPackage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_alpha.nefx2.sm51"] = new("Alpine", ["lotus"], "2026-renault"),
        ["object_auto_illuminate.nefx2.sm51"] = new("Aston Martin", ["force_india"], "2026-forceindia"),
        ["object_car_monitor.nefx2.sm51"] = new("Audi", ["sauber"], "2026-sauber"),
        ["object_driver_name_shader.nefx2.sm51"] = new("Cadillac", ["gm_cadillac"], "2026-cadillac"),
        ["object_flag.nefx2.sm51"] = new("Ferrari", ["ferrari"], "2026-ferrari"),
        ["object_glass.nefx2.sm51"] = new("Haas", ["haas"], "2026-haas"),
        ["object_jumbotron.nefx2.sm51"] = new("McLaren", ["mclaren"], "2026-mclaren"),
        ["object_mask.nefx2.sm51"] = new("Mercedes", ["mercedes"], "2026-mercedes"),
        ["object_opaque.nefx2.sm51"] = new("Racing Bulls", ["toro_rosso"], "2026-tororosso"),
        ["object_overlay.nefx2.sm51"] = new("Red Bull", ["redbull"], "2026-redbull"),
        ["object_roof.nefx2.sm51"] = new("Williams", ["williams"], "2026-williams"),
        ["object_my_team.nefx2.sm51"] = new("My Team",
            ["myteam_audi", "myteam_ferrari", "myteam_honda", "myteam_mercedes", "myteam_redbull_ford", "myteam_renault"],
            SuitSet: null),
    };

    /// <summary>The shader package in a .serps zip comment, or null.</summary>
    public static string? ShaderPackage(string? zipComment)
    {
        if (string.IsNullOrWhiteSpace(zipComment)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(zipComment);
            return doc.RootElement.TryGetProperty("erp_file_string", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The team a template is for (from its zip comment), or null for a shader package we don't know.</summary>
    public static Team? Of(SerpsTemplate tpl) => OfPackage(ShaderPackage(tpl.Comment));

    /// <summary>The team a Simplified Studio project is for: a project keeps its shader package in info.json.</summary>
    public static Team? Of(SimplifiedProject project) => OfPackage(project.ShaderPackageName);

    static Team? OfPackage(string? package) =>
        !string.IsNullOrEmpty(package) && ByShaderPackage.TryGetValue(package, out var t) ? t : null;
}
