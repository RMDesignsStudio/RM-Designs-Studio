using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using BCnEncoder.Decoder;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;

namespace HelmetSwap.Core;

/// <summary>A decoded texture: RGBA8, row-major, top row first.</summary>
public sealed class TextureImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }

    /// <summary>Bilinear-free nearest sample with wrap, u/v in texture space (0..1); returns RGBA as 0..1 floats.</summary>
    public (float R, float G, float B, float A) Sample(float u, float v)
    {
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        int x = Math.Min(Width - 1, (int)(u * Width)), y = Math.Min(Height - 1, (int)(v * Height));
        int i = (y * Width + x) * 4;
        return (Rgba[i] / 255f, Rgba[i + 1] / 255f, Rgba[i + 2] / 255f, Rgba[i + 3] / 255f);
    }

    /// <summary>
    /// Decode a game texture (GfxSRVResource + its surface, with the large mips from the .mipmaps file) at the largest
    /// mip level no bigger than <paramref name="maxSize"/>, as the Steering Wheel Swapper's report does (BCnEncoder).
    /// </summary>
    public static TextureImage Decode(GameInstall game, ErpResource srvRes, int maxSize = 1024, bool parallel = true)
    {
        var dds = HelmetTemplate.FullResolution(game, srvRes);
        using var ms = new MemoryStream();
        dds.Write(ms);
        ms.Position = 0;
        var decoder = new BcDecoder();
        decoder.Options.IsParallel = parallel;   // background pictures: one core, so the app stays responsive
        var levels = decoder.DecodeAllMipMaps2D(ms);
        var img = levels.FirstOrDefault(l => l.Width <= maxSize && l.Height <= maxSize);
        if (img.IsEmpty) img = levels[^1];
        var span = img.Span;
        var rgba = new byte[img.Width * img.Height * 4];
        for (int y = 0; y < img.Height; y++)
            MemoryMarshal.AsBytes(span.GetRowSpan(y)).CopyTo(rgba.AsSpan(y * img.Width * 4));
        return new TextureImage { Width = img.Width, Height = img.Height, Rgba = rgba };
    }

    /// <summary>Minimal RGBA PNG (no filtering), as in the Steering Wheel Swapper report.</summary>
    public byte[] ToPng()
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < Height; y++) { z.WriteByte(0); z.Write(Rgba, y * Width * 4, Width * 4); }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), Height);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr); Chunk(png, "IDAT", raw.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        s.Write(crc);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(byte[] d)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in d) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
