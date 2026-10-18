using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;

namespace HelmetSwap.Core;

/// <summary>
/// A SERPs template for one driver's helmet: the driver's helmet, wing, visor and visor-attachment textures, embedded
/// at full resolution under their own game names (so an unedited template changes nothing in-game). Layout follows
/// Team Simplified's v1.1 templates: ERPs/NN--NN--Section/NN--NN--26-name.erp + template.serps, zip comment = the ERP
/// SERPs injects into (the helmet shader, as the My Player Helmet template uses).
/// </summary>
public static class HelmetTemplate
{
    public const string InjectionTarget = "character_helmet.nefx2.sm51";
    public const string GameVersion = "1.0.138.5601";      // as in the current Team Simplified templates
    public const string Version = "v1.0";
    const string Section = "ERPs/01--00--Helmet/";

    static readonly Regex HelmetTexture = new(@"_(helmet|helmet_wing|visor|visor_attachments)_[a-z]+\.tif$", RegexOptions.Compiled);

    public static string TemplateName(Driver d) => $"{d.Year} {d.Label}{(d.Variant is null ? "" : " " + d.Variant)} Helmet - SERPs Template";
    public static string FileName(Driver d) => $"{TemplateName(d)} {Version}.serps";
    public static string EntryName(Driver d) => $"{Section}01--00--{d.Year % 100:00}-{d.Team.FileName}_{d.Slug}_helmet.erp";

    /// <summary>The driver's helmet textures, embedded at full resolution, in an ERP of their own.</summary>
    public static byte[] BuildErp(GameInstall game, Driver d, SwapLog log)
    {
        var erp = ErpX.Load(d.HelmetErp);
        var prefix = d.TextureRoot + d.Token + "_";
        var srvs = erp.Resources.Where(r => r.ResourceType == "GfxSRVResource" && r.Identifier.StartsWith(prefix, StringComparison.Ordinal)
                                            && HelmetTexture.IsMatch(r.Identifier)).ToList();
        if (srvs.Count == 0) throw new InvalidDataException($"{d.Name}: no helmet textures found in {Path.GetFileName(d.HelmetErp)}");
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var srvRes in srvs)
        {
            var dds = FullResolution(game, srvRes);
            var probe = new ErpGfxSRVResource();
            probe.FromResource(srvRes);
            var surface = erp.FindResource(probe.Fragment0.SurfaceResourceName);
            if (surface.Fragments.Count == 3) surface.Fragments.RemoveAt(2);   // no external-mipmaps table: embed the whole chain
            var srv = new ErpGfxSRVResource();
            srv.FromResource(srvRes);
            dds.ToErpGfxSRVResource(srv, null!, false, 0);
            srv.ToResource(srvRes);
            ErpX.Rehash(srvRes); ErpX.Rehash(surface);
            keep.Add(srvRes.Identifier); keep.Add(surface.Identifier);
            log.Info($"{srvRes.Identifier.Split('/')[^1]}: {dds.header.width}x{dds.header.height}");
        }
        erp.Resources.RemoveAll(r => !keep.Contains(r.Identifier));
        return ErpX.Save(erp);
    }

    /// <summary>A texture's full mip chain (the game keeps the large mips in a separate .mipmaps file).</summary>
    public static DdsFile FullResolution(GameInstall game, ErpResource srvRes)
    {
        var srv = new ErpGfxSRVResource();
        srv.FromResource(srvRes);
        Stream? mips = null;
        if (srv.SurfaceRes.HasMips)
        {
            var p = game.MipmapsPath(srv.SurfaceRes.Frag2.MipMapFileName);
            if (p is null || !srv.SurfaceRes.HasValidMips)
                throw new FileNotFoundException($"The full-resolution data of {srvRes.Identifier.Split('/')[^1]} is missing ({srv.SurfaceRes.Frag2.MipMapFileName}). Verify the game files in Steam.");
            mips = File.OpenRead(p);
        }
        using (mips) return srv.ToDdsFile(mips, false, 0);
    }

    /// <summary>Write the template .serps (to a temporary file that replaces the target only when complete).</summary>
    public static void Write(string outPath, Driver d, byte[] erp) => Write(outPath, TemplateName(d), EntryName(d), erp);

    /// <summary>Write a helmet .serps with the given template name and ERP entry.</summary>
    public static void Write(string outPath, string templateName, string entryName, byte[] erp) => Write(outPath, templateName, [(entryName, erp)]);

    /// <summary>Write a helmet .serps with the given template name and ERP entries ("ERPs/&lt;folder&gt;/&lt;name&gt;.erp", one
    /// folder each or shared; swap outputs have one per helmet part).</summary>
    public static void Write(string outPath, string templateName, IReadOnlyList<(string Entry, byte[] Erp)> entries)
    {
        var tmp = outPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                zip.Comment = JsonSerializer.Serialize(new { erp_file_string = InjectionTarget });
                zip.CreateEntry("ERPs/");
                foreach (var folder in entries.Select(e => e.Entry[..(e.Entry.LastIndexOf('/') + 1)]).Distinct()) zip.CreateEntry(folder);
                foreach (var (entryName, erp) in entries)
                    using (var s = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open()) s.Write(erp);
                using (var s = zip.CreateEntry("template.serps", CompressionLevel.Optimal).Open()) s.Write(Manifest(templateName));
            }
            File.Move(tmp, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>template.serps, formatted like Team Simplified's (UTF-8 with BOM, CRLF, 2-space indent). Author left blank.</summary>
    public static byte[] Manifest(string templateName)
    {
        var json = "{\r\n" +
                   $"  \"name\": \"{templateName}\",\r\n" +
                   "  \"author\": \"\",\r\n" +
                   $"  \"version\": \"{Version}\",\r\n" +
                   "  \"game\": \"F1® 25\",\r\n" +
                   $"  \"game_version\": \"{GameVersion}\",\r\n" +
                   "  \"studio_version\": \"2.0\"\r\n" +
                   "}";
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)];
    }

    /// <summary>Re-open a written template and check it against the game: layout, manifest, every texture full
    /// resolution, embedded, hashed, under the game's own name, and holding the game's own pixels.</summary>
    public static List<string> Verify(GameInstall game, Driver d, string path, out int checks)
    {
        var failures = new List<string>();
        int n = 0;
        void Check(bool ok, string failure) { n++; if (!ok) failures.Add(failure); }

        using var zip = ZipFile.OpenRead(path);
        Check(zip.Comment == JsonSerializer.Serialize(new { erp_file_string = InjectionTarget }), $"zip comment is '{zip.Comment}'");
        var manifest = zip.GetEntry("template.serps");
        Check(manifest is not null, "template.serps missing");
        if (manifest is not null)
        {
            using var ms = new MemoryStream(); using (var s = manifest.Open()) s.CopyTo(ms);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿'));
            Check(doc.RootElement.GetProperty("name").GetString() == TemplateName(d), "template.serps name is wrong");
            Check(doc.RootElement.GetProperty("author").GetString() == "", "template.serps author is not blank");
        }
        var entry = zip.GetEntry(EntryName(d));
        Check(entry is not null, $"{EntryName(d)} missing");
        if (entry is null) { checks = n; return failures; }
        byte[] data;
        using (var ms = new MemoryStream()) { using (var s = entry.Open()) s.CopyTo(ms); data = ms.ToArray(); }

        var outIdx = ErpX.Index(ErpX.Load(data));
        var gameIdx = ErpX.Index(ErpX.Load(d.HelmetErp));
        var srvs = outIdx.Values.Where(r => r.ResourceType == "GfxSRVResource").ToList();
        // exactly the driver's own helmet textures (where a driver has none of a kind, the game uses a shared
        // generic texture, which a per-driver template must not carry: editing it would change every driver)
        var prefix = d.TextureRoot + d.Token + "_";
        var expected = gameIdx.Values.Where(r => r.ResourceType == "GfxSRVResource" && r.Identifier.StartsWith(prefix, StringComparison.Ordinal)
                                                 && HelmetTexture.IsMatch(r.Identifier)).Select(r => r.Identifier).ToHashSet();
        Check(expected.SetEquals(srvs.Select(r => r.Identifier)), $"carries {srvs.Count} helmet textures, the game has {expected.Count} for this driver");
        Check(srvs.Any(r => r.Identifier.EndsWith("_helmet_d.tif", StringComparison.Ordinal)), "the main helmet texture (_helmet_d) is missing");
        Check(outIdx.Values.All(r => r.ResourceType is "GfxSRVResource" or "GfxSurfaceRes"), "the template carries something other than textures");
        foreach (var r in outIdx.Values) Check(ErpX.HashValid(r), $"{r.Identifier.Split('/')[^1]}: wrong hash");
        foreach (var r in srvs)
        {
            var name = r.Identifier.Split('/')[^1];
            Check(gameIdx.ContainsKey(r.Identifier), $"{name} is not a game texture name");
            var srv = new ErpGfxSRVResource(); srv.FromResource(r);
            Check(!srv.SurfaceRes.HasMips, $"{name} still streams external mipmaps");
            if (!gameIdx.TryGetValue(r.Identifier, out var g)) continue;
            var full = FullResolution(game, g);
            Check(srv.SurfaceRes.Width == full.header.width && srv.SurfaceRes.Height == full.header.height,
                  $"{name} is {srv.SurfaceRes.Width}x{srv.SurfaceRes.Height}, game is {full.header.width}x{full.header.height}");
            // the small mips the game keeps inside its ERP must be the tail of the embedded chain (same pixels)
            var gsrv = new ErpGfxSRVResource(); gsrv.FromResource(g);
            var gameSurface = gameIdx[gsrv.Fragment0.SurfaceResourceName];
            if (outIdx.TryGetValue(srv.Fragment0.SurfaceResourceName, out var outSurface))
                Check(ErpX.Data(outSurface, 1).AsSpan().EndsWith(ErpX.Data(gameSurface, 1)), $"{name} does not hold the game's pixels");
            else Check(false, $"{name}: surface missing");
        }
        checks = n;
        return failures;
    }
}
