using System.Buffers.Binary;
using System.Text;

namespace HelmetSwap.Core;

/// <summary>
/// The game's own text (localisation/&lt;build&gt;/language_eng.lng), for the names it shows: career helmets ("Teal",
/// "Heatsink"), replicas ("Lewis Hamilton (2008)"), the APXGP drivers. The file (big-endian): "LNGT", u32 size, then
/// tagged blocks (tag, u32 size, data; found by tag, as the sizes don't always chain): HSHS / HSHT (a djb2 hash table, not needed here), SIDA (u32 count, then per
/// string a u32 key offset into SIDB and a u32 text offset into LNGB), SIDB (the keys, "lng_tier01_livery52", 0-ended)
/// and LNGB (the texts, UTF-8, 0-ended).
/// </summary>
public sealed class GameText
{
    readonly Dictionary<string, string> _text;
    GameText(Dictionary<string, string> text) => _text = text;

    public int Count => _text.Count;
    public string? this[string key] => _text.GetValueOrDefault(key);

    /// <summary>The first of <paramref name="keys"/> the game has a text for, or null.</summary>
    public string? First(params string[] keys) => keys.Select(k => _text.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>The English text of the game, or null when it can't be read (names then fall back to the folders').</summary>
    public static GameText? Load(GameInstall game)
    {
        try
        {
            var dir = Path.Combine(game.GameDir, "localisation");
            var file = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "language_eng.lng", SearchOption.AllDirectories).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            return file is null ? null : Parse(File.ReadAllBytes(file));
        }
        catch { return null; }
    }

    public static GameText Parse(byte[] d)
    {
        if (d.Length < 8 || Encoding.ASCII.GetString(d, 0, 4) != "LNGT") throw new InvalidDataException("not a language file");
        // the blocks follow each other in this order (their sizes don't always chain exactly): found by tag
        int from = 8;
        (int Start, int Size) Block(string tag)
        {
            int p = d.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(tag));
            if (p < 0) throw new InvalidDataException($"language file: no {tag} block");
            p += from;
            from = p + 8;
            return (p + 8, (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4)));
        }
        var (sa, _) = Block("SIDA");
        var (sb, szb) = Block("SIDB");
        var (lb, szl) = Block("LNGB");
        string Str(int start, int size, int off, Encoding enc)
        {
            int s = start + off, e = Array.IndexOf(d, (byte)0, s, size - off);
            return enc.GetString(d, s, (e < 0 ? start + size : e) - s);
        }
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sa));
        var text = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int at = sa + 4 + i * 8;
            int ko = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at)), vo = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at + 4));
            if (ko >= szb || vo >= szl) continue;
            text[Str(sb, szb, ko, Encoding.ASCII)] = Str(lb, szl, vo, Encoding.UTF8);
        }
        return new GameText(text);
    }
}
