using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;

namespace Studio.Core;

/// <summary>An 8-bit RGBA image (row-major, no padding). Colour textures hold sRGB-encoded values, as in the files.</summary>
public sealed class RgbaImage(int width, int height, byte[]? px = null)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Px { get; } = px ?? new byte[width * height * 4];

    /// <summary>Whether any pixel is less than fully opaque.</summary>
    public bool HasTransparency()
    {
        for (int i = 3; i < Px.Length; i += 4) if (Px[i] != 255) return true;
        return false;
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Px.Clone());
}

/// <summary>An image file as read: its pixels, plus what it said about itself.</summary>
public sealed record ImageFile(RgbaImage Image, string Type, bool HasAlphaChannel, DdsFile? Dds = null)
{
    /// <summary>For a DDS: block format ("bc1", "bc3", "bc7", ..., or "rgba"), DXGI number and sRGB flag.</summary>
    public (string Format, int Dxgi, bool Srgb)? DdsFormat => Dds is null ? null : TextureIo.FormatOf(Dds);
}

/// <summary>
/// Reading, making and writing game textures: the game's own (full resolution, external .mipmaps included), image
/// files (PNG / JPEG / BMP / TIFF through Windows' codecs, DDS), mip chains, and block compression (BC1 / BC3 / BC4 /
/// BC5 / BC7) in exactly the format a texture already has.
/// </summary>
public static class TextureIo
{
    // ---------------------------------------------------------------- the game's textures

    /// <summary>A texture's full size, mip count and format, from its headers (no pixel data read).</summary>
    public static (int W, int H, int Mips, string Format, int Dxgi, bool Srgb) Info(ErpGfxSRVResource srv)
    {
        var s = srv.SurfaceRes;
        int w = (int)(s.HasMips ? s.MipWidth : s.Width), h = (int)(s.HasMips ? s.MipHeight : s.Height);
        int mips = (int)s.Fragment0.MipMapCount + s.Frag2.Mips.Count;
        var (f, d, srgb) = FormatOf(s.Fragment0.ImageType);
        return (w, h, Math.Max(1, mips), f, d, srgb);
    }

    /// <summary>The texture as a DDS at full resolution: the embedded levels plus the external .mipmaps data when the
    /// game has it (otherwise only the embedded, smaller levels).</summary>
    public static DdsFile Original(GameData game, ErpGfxSRVResource srv)
    {
        Stream? mips = null;
        if (srv.SurfaceRes.HasMips && srv.SurfaceRes.HasValidMips && game.MipmapsPath(srv.SurfaceRes.Frag2.MipMapFileName) is { } mp)
            mips = File.OpenRead(mp);
        using (mips) return srv.ToDdsFile(mips, false, 0);
    }

    public static (string Format, int Dxgi, bool Srgb) FormatOf(ErpGfxSurfaceFormat t) => t switch
    {
        ErpGfxSurfaceFormat.DXT1 => ("bc1", 71, false),
        ErpGfxSurfaceFormat.DXT1_SRGB => ("bc1", 72, true),
        ErpGfxSurfaceFormat.DXT5 => ("bc3", 77, false),
        ErpGfxSurfaceFormat.DXT5_SRGB => ("bc3", 78, true),
        ErpGfxSurfaceFormat.BC2_SRGB => ("bc2", 75, true),
        ErpGfxSurfaceFormat.ATI1 => ("bc4", 80, false),
        ErpGfxSurfaceFormat.ATI2 => ("bc5", 83, false),
        ErpGfxSurfaceFormat.BC7 => ("bc7", 98, false),
        ErpGfxSurfaceFormat.BC7_SRGB => ("bc7", 99, true),
        ErpGfxSurfaceFormat.ABGR8 => ("rgba", 28, false),
        _ => ("?", 0, false),
    };

    public static (string Format, int Dxgi, bool Srgb) FormatOf(DdsFile dds)
    {
        const uint DX10 = 0x30315844;
        uint cc = dds.header.ddspf.fourCC;
        if (cc == DX10)
        {
            int d = (int)dds.header10.dxgiFormat;
            return d switch
            {
                >= 70 and <= 72 => ("bc1", d, d == 72), >= 73 and <= 75 => ("bc2", d, d == 75), >= 76 and <= 78 => ("bc3", d, d == 78),
                >= 79 and <= 81 => ("bc4", d, false), >= 82 and <= 84 => ("bc5", d, false), >= 97 and <= 99 => ("bc7", d, d == 99),
                >= 27 and <= 32 => ("rgba", d, d == 29), 87 or 91 => ("bgra", d, d == 91),
                _ => ("dxgi " + d, d, false),
            };
        }
        return cc switch
        {
            0x31545844 => ("bc1", 71, false),                      // DXT1
            0x32545844 or 0x33545844 => ("bc2", 74, false),       // DXT2/3
            0x34545844 or 0x35545844 => ("bc3", 77, false),       // DXT4/5
            0x31495441 or 0x55344342 => ("bc4", 80, false),       // ATI1 / BC4U
            0x32495441 or 0x55354342 => ("bc5", 83, false),       // ATI2 / BC5U
            0 => ("rgba", 28, false),
            _ => ("?", 0, false),
        };
    }

    static int BlockBytes(string format) => format is "bc1" or "bc4" ? 8 : 16;

    static CompressionFormat Bcn(string format) => format switch
    {
        "bc1" => CompressionFormat.Bc1, "bc2" => CompressionFormat.Bc2, "bc3" => CompressionFormat.Bc3,
        "bc4" => CompressionFormat.Bc4, "bc5" => CompressionFormat.Bc5, "bc7" => CompressionFormat.Bc7,
        _ => throw new NotSupportedException($"textures in {format} format cannot be written"),
    };

    // ---------------------------------------------------------------- reading image files

    public static readonly string[] ImageExtensions = [".png", ".dds", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".tga"];

    /// <summary>Read a PNG / JPEG / BMP / TIFF (Windows' codecs, colour profile ignored so the values stay as
    /// painted) or a DDS (any BC format or RGBA, first level).</summary>
    public static ImageFile Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dds") return ReadDds(path);
        if (ext == ".tga") throw new NotSupportedException("TGA files are not supported: save the image as PNG (keeps transparency) or DDS.");
        using var fs = File.OpenRead(path);
        BitmapFrame frame;
        try
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = dec.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be read as an image ({ex.Message}).", ex);
        }
        var f = frame.Format;
        bool alpha = f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
                  || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float
                  || frame.Palette is { } pal && pal.Colors.Any(c => c.A < 255);
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);   // BGRA -> RGBA
        return new ImageFile(new RgbaImage(w, h, px), ext.TrimStart('.'), alpha);
    }

    static ImageFile ReadDds(string path)
    {
        DdsFile dds;
        try { using var fs = File.OpenRead(path); dds = new DdsFile(fs); }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a readable DDS file ({ex.Message}).", ex);
        }
        if (dds.header10.arraySize > 1) throw new InvalidDataException($"{Path.GetFileName(path)} is a texture array; a livery texture is a single image.");
        var img = Decode(dds);
        var (fmt, _, _) = FormatOf(dds);
        bool alpha = fmt is "bc2" or "bc3" or "bc7" or "rgba" or "bgra";
        return new ImageFile(img, "dds", alpha, dds);
    }

    /// <summary>The first level of a DDS as RGBA (BC4 = grey, BC5 = red/green).</summary>
    public static RgbaImage Decode(DdsFile dds)
    {
        int w = (int)dds.header.width, h = (int)dds.header.height;
        var (fmt, _, _) = FormatOf(dds);
        if (fmt is "rgba" or "bgra")
        {
            var px = dds.bdata.AsSpan(0, w * h * 4).ToArray();
            if (fmt == "bgra") for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return new RgbaImage(w, h, px);
        }
        int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BlockBytes(fmt);
        var colours = new BcDecoder().DecodeRaw(dds.bdata.AsSpan(0, size).ToArray(), w, h, Bcn(fmt));
        return new RgbaImage(w, h, MemoryMarshal.AsBytes(colours.AsSpan()).ToArray());
    }

    /// <summary>Save as PNG (RGBA, 8 bits per channel, no colour profile).</summary>
    public static void WritePng(RgbaImage img, string path)
    {
        var bgra = (byte[])img.Px.Clone();
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, bgra, img.Width * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, overwrite: true);
    }

    // ---------------------------------------------------------------- image operations

    static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i =>
    {
        float c = i / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    static byte ToSrgb(float l)
    {
        l = Math.Clamp(l, 0, 1);
        float c = l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(c * 255), 0, 255);
    }

    /// <summary>Resample to a new size (box filter when shrinking by whole factors, bilinear otherwise); colour is
    /// filtered in linear light for sRGB images, and weighted by alpha so see-through pixels don't bleed in.</summary>
    public static RgbaImage Resize(RgbaImage src, int w, int h, bool srgb)
    {
        if (src.Width == w && src.Height == h) return src.Clone();
        var img = src;
        while (img.Width >= w * 2 && img.Height >= h * 2 && img.Width % 2 == 0 && img.Height % 2 == 0) img = Half(img, srgb);
        if (img.Width == w && img.Height == h) return img;
        var dst = new RgbaImage(w, h);
        var s = img.Px; var d = dst.Px;
        int sw = img.Width, sh = img.Height;
        Parallel.For(0, h, y =>
        {
            float fy = Math.Clamp((y + 0.5f) * sh / h - 0.5f, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1); float ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * sw / w - 0.5f, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1); float tx = fx - x0;
                var acc = new float[4];
                float wsum = 0;
                foreach (var (xx, yy, wt) in new[] { (x0, y0, (1 - tx) * (1 - ty)), (x1, y0, tx * (1 - ty)), (x0, y1, (1 - tx) * ty), (x1, y1, tx * ty) })
                {
                    int o = (yy * sw + xx) * 4; float a = s[o + 3] / 255f, wa = wt * (a + 1e-4f);
                    for (int c = 0; c < 3; c++) acc[c] += wa * (srgb ? ToLinear[s[o + c]] : s[o + c] / 255f);
                    acc[3] += wt * a; wsum += wa;
                }
                int q = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float v = wsum > 1e-6f ? acc[c] / wsum : 0;
                    d[q + c] = srgb ? ToSrgb(v) : (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
                }
                d[q + 3] = (byte)Math.Clamp((int)MathF.Round(acc[3] * 255), 0, 255);
            }
        });
        return dst;
    }

    /// <summary>Half size, 2x2 average: colour in linear light for sRGB images and weighted by alpha (a decal's
    /// edge keeps its own colour instead of darkening towards the invisible pixels around it); alpha averaged.</summary>
    public static RgbaImage Half(RgbaImage src, bool srgb)
    {
        int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
        var dst = new RgbaImage(w, h);
        var s = src.Px; var d = dst.Px; int sw = src.Width, sh = src.Height;
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Min(y * 2, sh - 1), y1 = Math.Min(y * 2 + 1, sh - 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(x * 2, sw - 1), x1 = Math.Min(x * 2 + 1, sw - 1);
                float r = 0, g = 0, b = 0, aw = 0, asum = 0;
                foreach (var o in new[] { (y0 * sw + x0) * 4, (y0 * sw + x1) * 4, (y1 * sw + x0) * 4, (y1 * sw + x1) * 4 })
                {
                    float a = s[o + 3] / 255f, wt = a + 1e-4f;   // a hair of weight keeps fully clear areas' own colour
                    if (srgb) { r += wt * ToLinear[s[o]]; g += wt * ToLinear[s[o + 1]]; b += wt * ToLinear[s[o + 2]]; }
                    else { r += wt * s[o] / 255f; g += wt * s[o + 1] / 255f; b += wt * s[o + 2] / 255f; }
                    aw += wt; asum += a;
                }
                int q = (y * w + x) * 4;
                if (srgb) { d[q] = ToSrgb(r / aw); d[q + 1] = ToSrgb(g / aw); d[q + 2] = ToSrgb(b / aw); }
                else
                {
                    d[q] = (byte)Math.Clamp((int)MathF.Round(r / aw * 255), 0, 255);
                    d[q + 1] = (byte)Math.Clamp((int)MathF.Round(g / aw * 255), 0, 255);
                    d[q + 2] = (byte)Math.Clamp((int)MathF.Round(b / aw * 255), 0, 255);
                }
                d[q + 3] = (byte)Math.Clamp((int)MathF.Round(asum / 4 * 255), 0, 255);
            }
        });
        return dst;
    }

    /// <summary>The mip chain: level 0 and <paramref name="count"/> - 1 halvings (see <see cref="Half"/>).</summary>
    public static List<RgbaImage> Mips(RgbaImage level0, int count, bool srgb)
    {
        var l = new List<RgbaImage> { level0 };
        while (l.Count < count && (l[^1].Width > 1 || l[^1].Height > 1)) l.Add(Half(l[^1], srgb));
        return l;
    }

    /// <summary>
    /// Fill the colour of fully transparent pixels from the nearest visible ones (a push-pull pyramid), alpha
    /// unchanged. The game blends and shrinks textures without knowing which pixels are invisible, so whatever colour
    /// sits under alpha 0 (usually black or white from the paint program) bleeds into decal edges and distant mips;
    /// filled this way, the edges keep the decal's own colour.
    /// </summary>
    public static void Dilate(RgbaImage img)
    {
        int w0 = img.Width, h0 = img.Height;
        bool any = false, all = true;
        for (int i = 3; i < img.Px.Length; i += 4) { if (img.Px[i] > 0) any = true; else all = false; }
        if (!any || all) return;
        // pyramid above the image: (colour, covered) per pixel, a coarse pixel = the average of its covered children
        var levels = new List<(int W, int H, byte[] C, bool[] Cov)>();
        int pw = w0, ph = h0;
        Func<int, int, (int R, int G, int B)?> child = (x, y) =>
        {
            int o = (y * w0 + x) * 4;
            return img.Px[o + 3] > 0 ? (img.Px[o], img.Px[o + 1], img.Px[o + 2]) : null;
        };
        while (pw > 1 || ph > 1)
        {
            int w = (pw + 1) / 2, h = (ph + 1) / 2;
            var c = new byte[w * h * 3]; var v = new bool[w * h];
            int cpw = pw, cph = ph; var get = child;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = x * 2 + dx, sy = y * 2 + dy;
                            if (sx >= cpw || sy >= cph || get(sx, sy) is not { } p) continue;
                            r += p.R; g += p.G; b += p.B; n++;
                        }
                    if (n == 0) continue;
                    int di = y * w + x;
                    c[di * 3] = (byte)(r / n); c[di * 3 + 1] = (byte)(g / n); c[di * 3 + 2] = (byte)(b / n); v[di] = true;
                }
            });
            levels.Add((w, h, c, v));
            child = (x, y) => { int i = y * w + x; return v[i] ? (c[i * 3], c[i * 3 + 1], c[i * 3 + 2]) : null; };
            pw = w; ph = h;
        }
        // pull: an uncovered pixel takes its parent's colour, coarse to fine, then the image's clear pixels
        for (int li = levels.Count - 2; li >= 0; li--)
        {
            var (lw, lh, lc, lv) = levels[li];
            var (upw, _, uc, _) = levels[li + 1];
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x;
                    if (lv[i]) continue;
                    int p = (y / 2) * upw + x / 2;
                    lc[i * 3] = uc[p * 3]; lc[i * 3 + 1] = uc[p * 3 + 1]; lc[i * 3 + 2] = uc[p * 3 + 2];
                    lv[i] = true;
                }
        }
        var (l1w, _, l1c, _) = levels[0];
        Parallel.For(0, h0, y =>
        {
            for (int x = 0; x < w0; x++)
            {
                int o = (y * w0 + x) * 4;
                if (img.Px[o + 3] != 0) continue;
                int p = (y / 2) * l1w + x / 2;
                img.Px[o] = l1c[p * 3]; img.Px[o + 1] = l1c[p * 3 + 1]; img.Px[o + 2] = l1c[p * 3 + 2];
            }
        });
    }

    /// <summary>The plain background of an image without transparency: the colour most of its border shares (at least
    /// <paramref name="share"/> of the border pixels, within a small tolerance), or null when there is none.</summary>
    public static (byte R, byte G, byte B)? Background(RgbaImage img, double share = 0.6)
    {
        var counts = new Dictionary<int, int>();
        int total = 0;
        void Count(int x, int y)
        {
            int o = (y * img.Width + x) * 4;
            int key = (img.Px[o] >> 3) << 10 | (img.Px[o + 1] >> 3) << 5 | img.Px[o + 2] >> 3;   // 5 bits per channel
            counts[key] = counts.GetValueOrDefault(key) + 1; total++;
        }
        for (int x = 0; x < img.Width; x++) { Count(x, 0); Count(x, img.Height - 1); }
        for (int y = 1; y < img.Height - 1; y++) { Count(0, y); Count(img.Width - 1, y); }
        if (total == 0) return null;
        var (bin, n) = counts.MaxBy(kv => kv.Value);
        if (n < total * share) return null;
        // the exact colour: average of the border pixels in that bin
        long r = 0, g = 0, b = 0; int m = 0;
        void Avg(int x, int y)
        {
            int o = (y * img.Width + x) * 4;
            int key = (img.Px[o] >> 3) << 10 | (img.Px[o + 1] >> 3) << 5 | img.Px[o + 2] >> 3;
            if (key != bin) return;
            r += img.Px[o]; g += img.Px[o + 1]; b += img.Px[o + 2]; m++;
        }
        for (int x = 0; x < img.Width; x++) { Avg(x, 0); Avg(x, img.Height - 1); }
        for (int y = 1; y < img.Height - 1; y++) { Avg(0, y); Avg(img.Width - 1, y); }
        return ((byte)(r / m), (byte)(g / m), (byte)(b / m));
    }

    /// <summary>
    /// Turn a plain background into transparency. <paramref name="soft"/> = colour-to-alpha (exact for anti-aliased
    /// edges; colours close to the background become see-through, e.g. dark logos on black); otherwise pixels further
    /// than <paramref name="solidFrom"/> (0..1, per channel) from the background stay solid and only the band between
    /// <paramref name="clearBelow"/> and it fades, with the background's tint taken out of the edge colour.
    /// Returns the share of pixels made fully clear.
    /// </summary>
    public static double KeyOut(RgbaImage img, (byte R, byte G, byte B) key, bool soft, float clearBelow = 0.03f, float solidFrom = 0.3f)
    {
        float kr = key.R / 255f, kg = key.G / 255f, kb = key.B / 255f;
        int clear = 0;
        var px = img.Px;
        object gate = new();
        Parallel.For(0, img.Height, () => 0, (y, _, local) =>
        {
            for (int x = 0; x < img.Width; x++)
            {
                int o = (y * img.Width + x) * 4;
                float r = px[o] / 255f, g = px[o + 1] / 255f, b = px[o + 2] / 255f;
                float a;
                if (soft)
                {
                    static float A(float c, float k) => c > k ? (k < 1 ? (c - k) / (1 - k) : 0) : c < k ? (k > 0 ? (k - c) / k : 0) : 0;
                    a = MathF.Max(A(r, kr), MathF.Max(A(g, kg), A(b, kb)));
                    if (a < clearBelow) a = 0;
                }
                else
                {
                    float d = MathF.Max(MathF.Abs(r - kr), MathF.Max(MathF.Abs(g - kg), MathF.Abs(b - kb)));
                    a = Math.Clamp((d - clearBelow) / (solidFrom - clearBelow), 0, 1);
                }
                if (a <= 0) { px[o + 3] = 0; local++; continue; }
                // take the background's share out of the colour: c = k + (c - k) / a
                px[o] = (byte)Math.Clamp((int)MathF.Round((kr + (r - kr) / a) * 255), 0, 255);
                px[o + 1] = (byte)Math.Clamp((int)MathF.Round((kg + (g - kg) / a) * 255), 0, 255);
                px[o + 2] = (byte)Math.Clamp((int)MathF.Round((kb + (b - kb) / a) * 255), 0, 255);
                px[o + 3] = (byte)Math.Clamp((int)MathF.Round(a * px[o + 3]), 0, 255);
            }
            return local;
        }, local => { lock (gate) clear += local; });
        return (double)clear / (img.Width * img.Height);
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Block-compress every level into one DDS data blob (largest first), in parallel.</summary>
    public static byte[] Encode(IReadOnlyList<RgbaImage> levels, string format, bool bestQuality = false)
    {
        var parts = new byte[levels.Count][];
        var cf = Bcn(format);
        Parallel.For(0, levels.Count, i =>
        {
            var l = levels[i];
            var enc = new BcEncoder();
            enc.OutputOptions.Format = cf;
            enc.OutputOptions.GenerateMipMaps = false;
            enc.OutputOptions.Quality = bestQuality ? CompressionQuality.Balanced : CompressionQuality.Fast;
            enc.Options.IsParallel = l.Width * l.Height >= 256 * 256;
            parts[i] = enc.EncodeToRawBytes(l.Px, l.Width, l.Height, BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
        });
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }

    /// <summary>A DDS shaped like <paramref name="like"/> (same pixel format and header kind) holding new levels.</summary>
    public static DdsFile Like(DdsFile like, int w, int h, int mips, byte[] data)
    {
        var dds = new DdsFile();
        dds.header.flags = like.header.flags | DdsHeader.Flags.DDSD_MIPMAPCOUNT;
        dds.header.caps = like.header.caps | DdsHeader.Caps.DDSCAPS_MIPMAP | DdsHeader.Caps.DDSCAPS_COMPLEX;
        dds.header.ddspf.flags = like.header.ddspf.flags;
        dds.header.ddspf.fourCC = like.header.ddspf.fourCC;
        if (dds.header.ddspf.fourCC != 0) dds.header.ddspf.flags |= DdsPixelFormat.Flags.DDPF_FOURCC;
        dds.header.ddspf.rGBBitCount = like.header.ddspf.rGBBitCount;
        dds.header.ddspf.rBitMask = like.header.ddspf.rBitMask; dds.header.ddspf.gBitMask = like.header.ddspf.gBitMask;
        dds.header.ddspf.bBitMask = like.header.ddspf.bBitMask; dds.header.ddspf.aBitMask = like.header.ddspf.aBitMask;
        dds.header10.dxgiFormat = like.header10.dxgiFormat;
        dds.header10.arraySize = 1;
        dds.header.width = (uint)w; dds.header.height = (uint)h;
        dds.header.mipMapCount = (uint)mips;
        var (fmt, _, _) = FormatOf(like);
        dds.header.pitchOrLinearSize = fmt is "rgba" or "bgra" ? (uint)(w * 4) : (uint)(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BlockBytes(fmt));
        dds.bdata = data;
        return dds;
    }

    /// <summary>Replace a texture (SRV + its surface) inside an ERP with <paramref name="dds"/>, every level embedded
    /// (the external .mipmaps table is dropped, as the game's own files are not part of a mod).</summary>
    public static void Write(ErpFile erp, ErpResource srvRes, DdsFile dds)
    {
        var probe = new ErpGfxSRVResource();
        probe.FromResource(srvRes);
        var surface = erp.FindResource(probe.Fragment0.SurfaceResourceName);
        if (surface.Fragments.Count == 3) surface.Fragments.RemoveAt(2);
        var srv = new ErpGfxSRVResource();
        srv.FromResource(srvRes);
        dds.ToErpGfxSRVResource(srv, null!, false, 0);
        srv.ToResource(srvRes);
        ErpX.Rehash(srvRes); ErpX.Rehash(surface);
    }

    /// <summary>The levels of a block-compressed blob (for the viewer), largest first.</summary>
    public static List<byte[]> SplitLevels(byte[] data, string format, int w, int h, int mips)
    {
        var l = new List<byte[]>();
        int at = 0;
        for (int i = 0; i < mips; i++)
        {
            int lw = Math.Max(1, w >> i), lh = Math.Max(1, h >> i);
            int size = format is "rgba" or "bgra" ? lw * lh * 4 : Math.Max(1, (lw + 3) / 4) * Math.Max(1, (lh + 3) / 4) * BlockBytes(format);
            if (at + size > data.Length) break;
            l.Add(data.AsSpan(at, size).ToArray());
            at += size;
        }
        return l;
    }
}
