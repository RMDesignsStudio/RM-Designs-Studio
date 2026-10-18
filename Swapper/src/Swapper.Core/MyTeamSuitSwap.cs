using System.Text;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.Core;

/// <summary>What a My Team suit swap did, for the log and the checks.</summary>
public sealed class MyTeamSuitResult
{
    public required string Set { get; init; }
    public required string MyTeamToken { get; init; }
    public int Geometry { get; set; }
    public List<string> Models { get; } = [];
    public List<string> TexturesFilled { get; } = [];
    public List<string> TexturesAdded { get; } = [];
    public int MaterialsPointed { get; set; }
}

/// <summary>
/// Suits for My Team: any suit set (2024-2026, F1 and F2) on the My Team drivers. My Team's drivers wear the suit shell
/// <c>noyear-custom-myteam</c>, dressed by the template's driverwear part (its bindings for the My Team suit design it
/// targets, e.g. <c>tier02_livery98</c>, its <c>2026-myteam</c> materials and textures). The suit's own shape is the set's
/// (each maker's cut differs), so the swap, as RM Designs worked it out by hand and proved in game (LN4 Fusion, Williams
/// Sparco suits), is:
/// <list type="number">
/// <item>the template's driverwear part, kept whole (the bindings stay the template's);</item>
/// <item>the set's driver body and glove geometry (<c>condition_scene</c>) and its own suit, logo and glove-logo geometry
/// (<c>team_livery/SET/idf</c>), as the set has them;</item>
/// <item>the shell's six models (<c>noyear-custom-myteam_male</c>, <c>_female</c>, <c>_body_logos_*</c>,
/// <c>_glove_logos_*</c>), each with the set's model of that part (pointing at the set's geometry);</item>
/// <item>the template's materials that use the placeholder textures (<c>default-tif_*</c>) pointed at My Team textures of
/// the same name instead (same length: <c>default-tif</c> / <c>2026-myteam</c>), where the set has that texture;</item>
/// <item>every My Team texture filled with the set's picture of the same kind, at full size; the ones the template hasn't
/// got are added.</item>
/// </list>
/// </summary>
public static class MyTeamSuitSwap
{
    public const string Shell = "noyear-custom-myteam";
    const string Placeholder = "default-tif";
    public const string NoPart = "This My Team template has no My Team driverwear part (the one dressing the My Team suit) to give the new suits.";
    static readonly Regex MyTeamToken = new(@"team_livery/(20\d\d-myteam)/", RegexOptions.Compiled);

    /// <summary>The template's My Team driverwear part (the ERP binding the suit shell), or null when it has none.</summary>
    public static string? TemplatePart(SerpsTemplate tpl)
    {
        foreach (var n in tpl.EntryNames.Where(n => n.EndsWith(".erp", StringComparison.OrdinalIgnoreCase) && n.Contains("driverwear", StringComparison.OrdinalIgnoreCase)
                                                  && !n.Contains("_swapped", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            ErpFile erp;
            try { erp = ErpX.Load(tpl.Get(n)); } catch (InvalidDataException) { continue; }
            if (erp.Resources.Any(r => r.Identifier.Contains($"/team_livery/{Shell}/idf/", StringComparison.Ordinal) && r.ResourceType == "GfxMatBindRes")
                && erp.Resources.Any(r => MyTeamToken.IsMatch(r.Identifier)))
                return n;
        }
        return null;
    }

    public static (byte[] Erp, MyTeamSuitResult Result) Run(GameData game, byte[] driverwearErp, SuitSet wear, SwapLog log)
    {
        var entry = ErpX.Load(driverwearErp);
        var myTok = entry.Resources.Select(r => MyTeamToken.Match(r.Identifier)).First(m => m.Success).Groups[1].Value;
        var set = wear.Name;
        var donor = ErpX.Load(wear.ErpPath ?? SuitSwap.SetErp(game, set));
        var result = new MyTeamSuitResult { Set = set, MyTeamToken = myTok };
        var setGeo = $"/team_livery/{set}/idf/";

        // 1-2. the set's geometry: driver body and gloves, and its own suit / logo / glove-logo meshes
        foreach (var r in donor.Resources.ToList())
        {
            bool body = r.Identifier.Contains("/condition_scene/idf/", StringComparison.Ordinal) && r.ResourceType is "GfxBufferRes" or "GfxMeshRes" or "GfxModelRes" or "ModelProxyRes";
            bool own = r.Identifier.Contains(setGeo, StringComparison.Ordinal) && r.ResourceType is "GfxBufferRes" or "GfxMeshRes";
            if (!body && !own) continue;
            ErpX.AddOrReplace(entry, r);
            result.Geometry++;
        }

        // 3. the shell's models, each with the set's model of that part
        var part = new Regex($@"^eaid://character_package/team_livery/{Regex.Escape(set)}/idf/{Regex.Escape(set)}_(.+)\.idf\?(model|render)$");
        foreach (var r in donor.Resources.Where(r => r.ResourceType is "GfxModelRes" or "ModelProxyRes").ToList())
        {
            var m = part.Match(r.Identifier);
            if (!m.Success) continue;
            r.Identifier = $"eaid://character_package/team_livery/{Shell}/idf/{Shell}_{m.Groups[1].Value}.idf?{m.Groups[2].Value}";
            ErpX.Rehash(r);
            ErpX.AddOrReplace(entry, r);
            result.Models.Add($"{m.Groups[1].Value}?{m.Groups[2].Value}");
        }
        if (result.Models.Count == 0) throw new InvalidDataException($"{set} has no suit models to dress My Team's drivers with");

        // the set's textures by kind ("livery_v2_d", "glove_logo_n", ...)
        var setTex = new Regex($@"/team_livery/{Regex.Escape(set)}/textures/{Regex.Escape(set)}_(.+)\.tif$");
        var donorTex = donor.Resources.Where(r => r.ResourceType == "GfxSRVResource").Select(r => (r, m: setTex.Match(r.Identifier)))
                            .Where(x => x.m.Success).ToDictionary(x => x.m.Groups[1].Value, x => x.r.Identifier);

        // 4. materials on the placeholders -> My Team's own textures (where the set has the kind)
        var ph = new Regex($@"team_livery/{Placeholder}/textures/{Placeholder}_([a-z0-9_]+)\.tif");
        if (myTok.Length == Placeholder.Length)
            foreach (var r in entry.Resources.Where(r => r.ResourceType == "GfxMaterialRes"))
            {
                var data = ErpX.Data(r);
                var text = Encoding.Latin1.GetString(data);
                bool changed = false;
                foreach (Match m in ph.Matches(text))
                {
                    if (!donorTex.ContainsKey(m.Groups[1].Value)) continue;
                    var to = Encoding.Latin1.GetBytes($"team_livery/{myTok}/textures/{myTok}_{m.Groups[1].Value}.tif");
                    to.CopyTo(data, m.Index);
                    changed = true;
                }
                if (!changed) continue;
                r.Fragments[0].SetData(data, true);
                ErpX.Rehash(r);
                result.MaterialsPointed++;
            }

        // 5. every My Team texture the materials use: the set's picture, full size (added where the template has none)
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        var mine = new Regex($@"team_livery/{Regex.Escape(myTok)}/textures/{Regex.Escape(myTok)}_([a-z0-9_]+)\.tif");
        foreach (var r in entry.Resources.Where(r => r.ResourceType == "GfxMaterialRes"))
            foreach (Match m in mine.Matches(Encoding.Latin1.GetString(ErpX.Data(r)))) wanted.Add(m.Groups[1].Value);
        foreach (var r in entry.Resources.Where(r => r.ResourceType == "GfxSRVResource"))
            if (mine.Match(r.Identifier) is { Success: true } m) wanted.Add(m.Groups[1].Value);
        foreach (var kind in wanted)
        {
            if (!donorTex.TryGetValue(kind, out var srcId)) { log.Warn($"Suits: {set} has no '{kind}' texture; My Team's own is kept."); continue; }
            var id = $"eaid://character_package/team_livery/{myTok}/textures/{myTok}_{kind}.tif";
            var src = donor.Resources.First(r => r.Identifier == srcId);
            var srcSrv = new ErpGfxSRVResource();
            srcSrv.FromResource(src);
            EgoEngineLibrary.Graphics.Dds.DdsFile dds;
            var mips = srcSrv.SurfaceRes.HasMips ? ChassisSwap.OwnMipmaps(game, srcSrv) : null;
            using (Stream? s = mips is null ? null : File.OpenRead(mips)) dds = srcSrv.ToDdsFile(s, false, 0);
            var srv = entry.Resources.FirstOrDefault(r => r.Identifier == id);
            if (srv is null)
            {
                // a slot of its own: the set's texture and image under My Team's names
                var surfaceSrc = donor.Resources.First(r => r.Identifier == srcSrv.Fragment0.SurfaceResourceName);
                var fresh = ErpX.Load(wear.ErpPath ?? SuitSwap.SetErp(game, set));
                var newSrv = fresh.Resources.First(r => r.Identifier == srcId);
                var newSurface = fresh.Resources.First(r => r.Identifier == surfaceSrc.Identifier);
                newSurface.Identifier = id + ".image";
                var f0 = new ErpGfxSRVResource0();
                f0.FromFragment(newSrv.GetFragment("temp", 0));
                f0.SurfaceResourceName = newSurface.Identifier;
                f0.ToFragment(newSrv.GetFragment("temp", 0));
                newSrv.Identifier = id;
                ErpX.AddOrReplace(entry, newSurface);
                ErpX.AddOrReplace(entry, newSrv);
                srv = newSrv;
                result.TexturesAdded.Add(kind);
            }
            else result.TexturesFilled.Add(kind);
            TextureSwap.Apply(entry, srv, dds);
        }
        log.Info($"Suits on My Team: {set}'s suit ({result.Models.Count / 2} parts) on the {Shell} shell, {result.Geometry} geometry resources; " +
                 $"textures filled {result.TexturesFilled.Count}, added {result.TexturesAdded.Count}; {result.MaterialsPointed} materials pointed at My Team textures.");
        return (ErpX.Save(entry), result);
    }

    /// <summary>The finished ERP read back: it loads; every shell model it carries points at meshes in it; every mesh's
    /// buffers are in it; every My Team texture a material uses is in it, fully embedded. Returns the number of checks.</summary>
    public static int Verify(byte[] erpBytes, MyTeamSuitResult result)
    {
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) throw new InvalidDataException($"My Team suits check failed: {what}"); }
        ErpFile erp;
        try { erp = ErpX.Load(erpBytes); }
        catch (InvalidDataException ex) { throw new InvalidDataException("My Team suits check failed: the driverwear can't be read back: " + ex.Message, ex); }
        Check(true, "loads");
        var ids = erp.Resources.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal);
        int dangling = 0;
        foreach (var r in erp.Resources)
        {
            if (r.ResourceType == "GfxMeshRes")
                dangling += ErpX.Eaids(ErpX.Data(r)).Count(e => Regex.IsMatch(e, @"&(vb|ib)=\d+$") && !ids.Contains(e));
            if (r.ResourceType is "GfxModelRes" or "ModelProxyRes" && r.Identifier.Contains($"/team_livery/{Shell}/", StringComparison.Ordinal))
                dangling += r.Fragments.Sum(f => ErpX.Eaids(f.GetDataArray(true)).Count(e => e.EndsWith("&mesh", StringComparison.Ordinal) && !ids.Contains(e)));
        }
        Check(dangling == 0, $"{dangling} geometry references point at nothing");
        Check(result.Models.All(m => ids.Contains($"eaid://character_package/team_livery/{Shell}/idf/{Shell}_{m.Replace("?", ".idf?")}")), "a shell model is missing");
        var mine = new Regex($@"eaid://character_package/team_livery/{Regex.Escape(result.MyTeamToken)}/textures/[a-z0-9_\-]+\.tif");
        foreach (var r in erp.Resources.Where(r => r.ResourceType == "GfxMaterialRes"))
            foreach (var t in mine.Matches(Encoding.Latin1.GetString(ErpX.Data(r))).Select(m => m.Value).Distinct())
                Check(ids.Contains(t), $"{t.Split('/')[^1]} is used but not in the driverwear");
        foreach (var k in result.TexturesFilled.Concat(result.TexturesAdded))
        {
            var srv = new ErpGfxSRVResource();
            srv.FromResource(erp.Resources.First(r => r.Identifier == $"eaid://character_package/team_livery/{result.MyTeamToken}/textures/{result.MyTeamToken}_{k}.tif"));
            Check(!srv.SurfaceRes.HasMips, $"{k} still needs an external .mipmaps file");
        }
        return checks;
    }
}
