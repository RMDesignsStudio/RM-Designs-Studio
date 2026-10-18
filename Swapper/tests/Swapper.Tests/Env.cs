namespace Swapper.Tests;

/// <summary>
/// Where the tests find the game and the templates. Override with SWAPPER_GAME / SWAPPER_TEMPLATES. Tests skip (never
/// fail) when a location is missing. Every input is only read.
/// </summary>
static class Env
{
    public static string GameDir => Var("SWAPPER_GAME", "");
    public static string TemplateDir => Var("SWAPPER_TEMPLATES", "");
    /// <summary>Where the mods (Simplified Studio projects) are.</summary>
    public static string ModsDir => Var("SWAPPER_MODS", "");

    static string Var(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    static GameData? _game;
    public static GameData Game()
    {
        if (!Directory.Exists(Path.Combine(GameDir, "2025_asset_groups"))) Assert.Skip($"F1 25 not found at {GameDir} (set SWAPPER_GAME)");
        return _game ??= new GameData(GameDir);
    }

    public static string Project(string relative)
    {
        var p = Path.Combine(ModsDir, relative);
        if (!File.Exists(p)) Assert.Skip($"project not found: {p}");
        return p;
    }

    public static string Template(string file)
    {
        var p = Path.Combine(TemplateDir, file);
        if (!File.Exists(p)) Assert.Skip($"template not found: {p}");
        return p;
    }
}
