using System.IO.Compression;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;

namespace HelmetSwap.Core;

/// <summary>
/// The layout of a swap's .serps: one folder per helmet part, each with its own ERP, so SERPs Studio lists the parts
/// apart, and a part that came from another driver says whose, as the Steering Wheel Swapper marks a swapped wheel:
/// <code>
/// ERPs/01--00--Base/                         the design (helmet material + textures, the shell, material bindings)
/// ERPs/02--00--Visor - 25 Max Verstappen/    the visor and tear-offs
/// ERPs/03--00--Visor Strip/
/// ERPs/04--00--Wing - 25 Max Verstappen/
/// ERPs/05--00--Attachments/
/// ERPs/06--00--3D Model - 25 Max Verstappen/ the helmet parts model (wing, strip and attachment shapes, another
///                                            make's shell), when its geometry changes
/// </code>
/// Every part of the finished helmet is in the file: a part that isn't swapped carries the driver's own textures at
/// full resolution under their game names (as a template does), so the whole helmet can be edited. A part with
/// nothing of its own (e.g. attachments in the game's shared clear plastic) has no folder.
/// </summary>
public static class HelmetSerps
{
    public sealed record Section(int Order, string Name, string Key, SwapParts? Part);

    public static readonly Section[] Sections =
    [
        new(1, "Base", "base", SwapParts.Design),
        new(2, "Visor", "visor", SwapParts.Visor),
        new(3, "Visor Strip", "visor-strip", SwapParts.VisorStrip),
        new(4, "Wing", "wing", SwapParts.Wing),
        new(5, "Attachments", "attachments", SwapParts.Attachments),
        new(6, "3D Model", "model", null),
    ];

    /// <summary>"25 Max Verstappen", "26 Charles Leclerc", "25 Max Verstappen Japan", "Lewis Hamilton 2008 Replica".</summary>
    public static string DonorTag(Driver d) =>
        (d.IsCareer ? "" : $"{d.Year % 100:00} ") + d.Name + (d.Variant is null ? "" : " " + d.Variant);

    /// <summary>"ERPs/04--00--Wing - 25 Max Verstappen/" for a part taken from the donor, "ERPs/04--00--Wing/" otherwise;
    /// the 3D model is marked when it holds the donor's shapes (wing, visor strip, attachments or shell).</summary>
    public static string Folder(Section s, Driver target, Driver donor, SwapParts parts) => Folder(s, target, PartPicks.Of(donor, parts));

    /// <summary>Parts from several helmets: each folder names its own donor; the 3D model names every helmet whose shapes
    /// it holds ("3D Model - 26 Nico Hulkenberg, 24 Isack Hadjar").</summary>
    public static string Folder(Section s, Driver target, PartPicks picks)
    {
        var from = s.Part is { } p ? (picks[p] is { } d ? [d] : new List<Driver>())
            : new[] { SwapParts.Wing, SwapParts.VisorStrip, SwapParts.Attachments }.Select(x => picks[x])
                  .Append(HelmetSwapper.ShellSwap(target, picks) ? picks[SwapParts.Design] : null)
                  .OfType<Driver>().Distinct().ToList();
        return $"ERPs/{s.Order:00}--00--{s.Name}{(from.Count > 0 ? " - " + string.Join(", ", from.Select(DonorTag)) : "")}/";
    }

    public static string EntryName(Section s, Driver target, Driver donor, SwapParts parts) => EntryName(s, target, PartPicks.Of(donor, parts));
    public static string EntryName(Section s, Driver target, PartPicks picks) =>
        $"{Folder(s, target, picks)}01--00--{target.Year % 100:00}-{target.Team.FileName}_{target.Slug}_helmet-{s.Key}.erp";

    /// <summary>The section of a helmet slot's material ("visor_tear" -> Visor, "helmet_transparent" -> Attachments).</summary>
    static Section OfSlot(string slot) => Sections.First(s => s.Part == HelmetSwapper.PartOf(slot));

    /// <summary>
    /// The finished helmet's resources, by part: the swap's own (materials, private textures, bindings, the parts model)
    /// plus the target's own textures that the finished helmet still uses, embedded at full resolution.
    /// </summary>
    public static List<(Section Section, string Entry, byte[] Erp)> Entries(GameInstall game, Driver target, Driver donor, SwapParts parts, byte[] swapErp, SwapLog log) =>
        Entries(game, target, PartPicks.Of(donor, parts), swapErp, log);

    public static List<(Section Section, string Entry, byte[] Erp)> Entries(GameInstall game, Driver target, PartPicks picks, byte[] swapErp, SwapLog log)
    {
        var swap = ErpX.Load(swapErp);
        var outIdx = ErpX.Index(swap);
        var placed = Place(game, target, outIdx, out var ownTextures);

        // the target's own textures the finished helmet still uses (full resolution, game names)
        var own = new List<(ErpResource Res, Section Section)>();
        ownTextures.RemoveAll(t => outIdx.ContainsKey(t.Texture));
        foreach (var (texId, section) in ownTextures)
            foreach (var r in OwnTexture(game, target, texId)) own.Add((r, section));
        if (ownTextures.Count > 0)
            log.Info($"{target.Name}'s own textures kept for every part not swapped: {ownTextures.Count} at full resolution.");

        var result = new List<(Section, string, byte[])>();
        foreach (var s in Sections)
        {
            var mine = swap.Resources.Where(r => placed[r.Identifier] == s).Concat(own.Where(o => o.Section == s).Select(o => o.Res)).ToList();
            if (mine.Count == 0) continue;
            var erp = ErpX.Load(swapErp);                     // the container (header kept)
            erp.Resources.Clear();
            foreach (var r in mine) ErpX.AddOrReplace(erp, r);
            result.Add((s, EntryName(s, target, picks), ErpX.Save(erp)));
        }
        return result;
    }

    /// <summary>
    /// Which section each resource of the swap goes to, and the target's own textures (not in the swap) that the
    /// finished helmet's materials use, with their sections. Materials go by the slot that binds them; a texture by
    /// the first slot (base, visor, visor strip, wing, attachments) whose material uses it; the hidden-shell materials
    /// and the material bindings with the base; the parts model's mesh, buffers and model with the 3D model.
    /// </summary>
    public static Dictionary<string, Section> Place(GameInstall game, Driver target, Dictionary<string, ErpResource> outIdx,
        out List<(string Texture, Section Section)> ownTextures)
    {
        var tGame = ErpX.Index(ErpX.Load(target.HelmetErp));
        foreach (var f in HelmetSwapper.ReadErps(game, target)) foreach (var r in ErpX.Load(f).Resources) tGame.TryAdd(r.Identifier, r);
        var common = ErpX.Index(ErpX.Load(HelmetSwapper.CommonHelmetErp(game)));
        var live = new Dictionary<string, ErpResource>(tGame);
        foreach (var (k, v) in outIdx) live[k] = v;

        var placed = new Dictionary<string, Section>();
        var texSection = new Dictionary<string, Section>();
        // the slots in section order, so a texture two slots share goes with the first (the wing texture: wing, then vents)
        foreach (var slot in HelmetSwapper.Parts.OrderBy(p => OfSlot(p).Order))
        {
            string mat;
            try { mat = HelmetSwapper.BoundMaterial(live, target, slot); } catch { continue; }
            var s = OfSlot(slot);
            if (outIdx.ContainsKey(mat)) placed.TryAdd(mat, s);
            var res = live.GetValueOrDefault(mat) ?? common.GetValueOrDefault(mat);
            if (res is null) continue;
            foreach (var tex in ErpX.Eaids(ErpX.Data(res)).Where(e => e.EndsWith(".tif", StringComparison.Ordinal)))
                texSection.TryAdd(tex, s);
        }
        var model = HelmetSwapper.Attachments(target);
        var baseSection = Sections[0];
        foreach (var r in outIdx.Values)
        {
            if (placed.ContainsKey(r.Identifier)) continue;
            var id = r.Identifier;
            if (r.ResourceType == "GfxSRVResource" && texSection.TryGetValue(id, out var ts)) placed[id] = ts;
            else if (r.ResourceType == "GfxSurfaceRes" && id.EndsWith(".image", StringComparison.Ordinal) && texSection.TryGetValue(id[..^6], out var ss)) placed[id] = ss;
            else if (id.StartsWith(model, StringComparison.Ordinal) || id.Contains("/idf/hsw", StringComparison.Ordinal)) placed[id] = Sections[^1];
            else placed[id] = baseSection;                // material bindings, the hidden-shell materials
        }
        // a private texture's surface is named after the texture; a game texture's surface by the SRV
        foreach (var r in outIdx.Values.Where(r => r.ResourceType == "GfxSRVResource"))
        {
            var srv = new ErpGfxSRVResource(); srv.FromResource(r);
            if (outIdx.ContainsKey(srv.Fragment0.SurfaceResourceName)) placed[srv.Fragment0.SurfaceResourceName] = placed[r.Identifier];
        }

        ownTextures = texSection.Where(t => target.OwnsTexture(t.Key) && !HelmetSwapper.IsPrivateFor(t.Key, target) && tGame.ContainsKey(t.Key))
                                .Select(t => (t.Key, t.Value)).ToList();
        return placed;
    }

    /// <summary>One of the target's own textures, SRV + surface, with its full mip chain embedded (as a template has it).</summary>
    static IEnumerable<ErpResource> OwnTexture(GameInstall game, Driver target, string texId)
    {
        foreach (var path in new[] { target.HelmetErp }.Concat(HelmetSwapper.ReadErps(game, target)))
        {
            var erp = ErpX.Load(path);
            var srvRes = erp.Resources.FirstOrDefault(r => r.Identifier == texId);
            if (srvRes is null) continue;
            var dds = HelmetTemplate.FullResolution(game, srvRes);
            var probe = new ErpGfxSRVResource(); probe.FromResource(srvRes);
            var surface = erp.FindResource(probe.Fragment0.SurfaceResourceName);
            if (surface.Fragments.Count == 3) surface.Fragments.RemoveAt(2);   // no external-mipmaps table: embed the whole chain
            var srv = new ErpGfxSRVResource(); srv.FromResource(srvRes);
            dds.ToErpGfxSRVResource(srv, null!, false, 0);
            srv.ToResource(srvRes);
            ErpX.Rehash(srvRes); ErpX.Rehash(surface);
            return [srvRes, surface];
        }
        throw new InvalidDataException($"{target.Name}: texture {texId.Split('/')[^1]} not found");
    }

    /// <summary>Every helmet ERP in a written .serps, merged (a resource in two entries is reported).</summary>
    public static Dictionary<string, ErpResource> ReadAll(ZipArchive zip, Action<bool, string> check, out Dictionary<string, string> entryOf)
    {
        var all = new Dictionary<string, ErpResource>(StringComparer.Ordinal);
        entryOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("ERPs/", StringComparison.Ordinal) && e.FullName.EndsWith(".erp", StringComparison.Ordinal)))
        {
            byte[] data;
            using (var ms = new MemoryStream()) { using (var s = e.Open()) s.CopyTo(ms); data = ms.ToArray(); }
            foreach (var r in ErpX.Load(data).Resources)
            {
                check(all.TryAdd(r.Identifier, r), $"{r.Identifier.Split('/')[^1]} is in two ERPs");
                entryOf.TryAdd(r.Identifier, e.FullName);
            }
        }
        return all;
    }
}
