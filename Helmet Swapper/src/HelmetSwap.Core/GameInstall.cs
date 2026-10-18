using System.Text.RegularExpressions;

namespace HelmetSwap.Core;

/// <summary>An F1 25 install and where its character (driver) files live.</summary>
public sealed class GameInstall
{
    public string GameDir { get; }
    public string Assets => Path.Combine(GameDir, "2025_asset_groups");
    public string DriversDir => Path.Combine(Assets, "character_package", "drivers");

    public GameInstall(string gameDir)
    {
        GameDir = gameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            throw new DirectoryNotFoundException($"The F1 25 game folder was not found: {gameDir}. Use Detect or Browse… to pick it.");
        if (!Directory.Exists(DriversDir))
            throw new DirectoryNotFoundException($"{gameDir} is not an F1 25 game folder (2025_asset_groups\\character_package\\drivers is missing). " +
                                                 "Pick the folder that contains F1_25.exe.");
    }

    /// <summary>Full path of a texture's external .mipmaps file (paths in the ERP are package-relative).</summary>
    public string? MipmapsPath(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        foreach (var root in new[] { Assets, GameDir })
        {
            var p = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Best-effort Steam library scan for "F1 25".</summary>
    public static string? Detect()
    {
        var candidates = new List<string>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Steam",
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Steam",
                 })
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                candidates.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            candidates.Add(root);
        }
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            candidates.Add(Path.Combine(d.Name, "SteamLibrary"));
            candidates.Add(Path.Combine(d.Name, "Program Files (x86)", "Steam"));
        }
        foreach (var lib in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(lib, "steamapps", "common", "F1 25");
            if (Directory.Exists(Path.Combine(dir, "2025_asset_groups", "character_package", "drivers"))) return dir;
        }
        return null;
    }
}
