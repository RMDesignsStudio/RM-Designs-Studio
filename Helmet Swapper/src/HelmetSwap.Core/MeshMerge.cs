using System.Buffers.Binary;
using System.Text;

namespace HelmetSwap.Core;

/// <summary>
/// F1 25 GfxMeshRes descriptor, enough to merge two meshes. Layout (offsets relative to 76 unless noted):
/// header u32[19]: [0] submeshes, [1] streams, [2] 1, [3] [4] submeshes, [5] stream-name table, [6] index-buffer name,
/// [7] slot-name table, [8] per-submesh node table (8 bytes: node, submesh), [9] streams, [10] per-stream formats
/// (20 bytes), [11] per-stream slots (8 bytes: 1, stream), [12] node groups, [13] groups (92 bytes: 4x4 matrix,
/// 0, node, 0, first submesh, submesh count, 0, 0), [14] submeshes, [15] per-submesh ranges (16 bytes: opaque flag,
/// submesh, index offset, index count), [16..18] (18 = LOD).
/// Submesh records from absolute 76, 88 bytes: u32[3..5] index offset/count/base vertex, f32[6..9] bounding sphere,
/// u32[10] offset into the node table, [12] slot-name offset, [14..19] the six stream indices.
/// </summary>
public sealed class MeshDesc
{
    public uint[] Header = new uint[19];
    public List<byte[]> Records = [];          // 88 bytes each
    public List<byte[]> Groups = [];           // 92 bytes each
    public List<byte[]> Ranges = [];           // 16 bytes each
    public List<string> Streams = [];          // vertex buffer names
    public string IndexBuffer = "";
    public List<string> Slots = [];            // per submesh
    public List<(uint Node, uint Sub)> Nodes = [];
    public List<byte[]> Formats = [];          // 20 bytes each
    public List<(uint A, uint Stream)> StreamSlots = [];

    const int B = 76;
    static uint U(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    static void W(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
    static string Cstr(byte[] d, ref int p) { int e = Array.IndexOf(d, (byte)0, p); var s = Encoding.ASCII.GetString(d, p, e - p); p = e + 1; return s; }

    public static MeshDesc Parse(byte[] d)
    {
        var m = new MeshDesc();
        for (int i = 0; i < 19; i++) m.Header[i] = U(d, i * 4);
        var h = m.Header;
        int nsub = (int)h[0], nstream = (int)h[1];
        // h[3]: surface names (the My Team parts model shares names between surfaces: read-only here)
        if (h[3] > nsub || h[3] == 0 || h[4] != nsub || h[14] != nsub || h[9] != nstream || h[13] != nsub * 88)
            throw new InvalidDataException("unexpected mesh layout");
        for (int i = 0; i < nsub; i++) m.Records.Add(d[(B + i * 88)..(B + i * 88 + 88)]);
        for (int i = 0; i < h[12]; i++) m.Groups.Add(d[(B + (int)h[13] + i * 92)..(B + (int)h[13] + i * 92 + 92)]);
        for (int i = 0; i < nsub; i++) m.Ranges.Add(d[(B + (int)h[15] + i * 16)..(B + (int)h[15] + i * 16 + 16)]);
        int p = B + (int)h[5];
        for (int i = 0; i < nstream; i++) m.Streams.Add(Cstr(d, ref p));
        if (p != B + h[6]) throw new InvalidDataException("unexpected mesh layout (stream names)");
        m.IndexBuffer = Cstr(d, ref p);
        for (int i = 0; i < nsub; i++)
        {
            int q = B + (int)h[7] + (int)U(m.Records[i], 48);
            m.Slots.Add(Cstr(d, ref q));
        }
        for (int i = 0; i < nsub; i++) m.Nodes.Add((U(d, B + (int)h[8] + i * 8), U(d, B + (int)h[8] + i * 8 + 4)));
        for (int i = 0; i < nstream; i++) m.Formats.Add(d[(B + (int)h[10] + i * 20)..(B + (int)h[10] + i * 20 + 20)]);
        for (int i = 0; i < nstream; i++) m.StreamSlots.Add((U(d, B + (int)h[11] + i * 8), U(d, B + (int)h[11] + i * 8 + 4)));
        if (B + h[11] + nstream * 8 != d.Length) throw new InvalidDataException("unexpected mesh layout (size)");
        m.SharedNames = h[3] != nsub;
        return m;
    }

    public int[] StreamsOf(int sub) => Enumerable.Range(0, 6).Select(k => (int)U(Records[sub], 56 + k * 4)).ToArray();
    public (int Offset, int Count) IndexRange(int sub) => ((int)U(Records[sub], 12), (int)U(Records[sub], 16));

    /// <summary>Surfaces share names (the My Team parts model): it can be read, not written back.</summary>
    public bool SharedNames { get; private set; }

    public byte[] Write()
    {
        if (SharedNames) throw new InvalidOperationException("this mesh shares surface names between surfaces: it can't be rebuilt");
        int nsub = Records.Count, nstream = Streams.Count;
        var names = new List<byte>(); var nameOff = new int[nsub];
        for (int i = 0; i < nsub; i++) { nameOff[i] = names.Count; names.AddRange(Encoding.ASCII.GetBytes(Slots[i])); names.Add(0); }
        var streamBytes = new List<byte>();
        foreach (var s in Streams) { streamBytes.AddRange(Encoding.ASCII.GetBytes(s)); streamBytes.Add(0); }
        var ib = Encoding.ASCII.GetBytes(IndexBuffer + "\0");

        int recs = 0, groups = nsub * 88, ranges = groups + Groups.Count * 92, streams = ranges + nsub * 16;
        int ibOff = streams + streamBytes.Count, slots = ibOff + ib.Length, nodes = slots + names.Count;
        int formats = nodes + nsub * 8, sslots = formats + nstream * 20, end = sslots + nstream * 8;
        var d = new byte[B + end];
        var h = (uint[])Header.Clone();
        h[0] = h[3] = h[4] = h[14] = (uint)nsub; h[1] = h[9] = (uint)nstream;
        h[5] = (uint)streams; h[6] = (uint)ibOff; h[7] = (uint)slots; h[8] = (uint)nodes; h[10] = (uint)formats; h[11] = (uint)sslots;
        h[12] = (uint)Groups.Count; h[13] = (uint)groups; h[15] = (uint)ranges;
        for (int i = 0; i < 19; i++) W(d, i * 4, h[i]);
        for (int i = 0; i < nsub; i++)
        {
            var r = (byte[])Records[i].Clone();
            W(r, 40, (uint)(i * 8)); W(r, 48, (uint)nameOff[i]);
            r.CopyTo(d, B + recs + i * 88);
        }
        for (int i = 0; i < Groups.Count; i++) Groups[i].CopyTo(d, B + groups + i * 92);
        for (int i = 0; i < nsub; i++) Ranges[i].CopyTo(d, B + ranges + i * 16);
        streamBytes.ToArray().CopyTo(d, B + streams);
        ib.CopyTo(d, B + ibOff);
        names.ToArray().CopyTo(d, B + slots);
        for (int i = 0; i < nsub; i++) { W(d, B + nodes + i * 8, Nodes[i].Node); W(d, B + nodes + i * 8 + 4, Nodes[i].Sub); }
        for (int i = 0; i < nstream; i++) Formats[i].CopyTo(d, B + formats + i * 20);
        for (int i = 0; i < nstream; i++) { W(d, B + sslots + i * 8, StreamSlots[i].A); W(d, B + sslots + i * 8 + 4, StreamSlots[i].Stream); }
        return d;
    }

    /// <summary>
    /// <paramref name="a"/> followed by <paramref name="b"/>'s submeshes: b's streams are appended, b's indices follow
    /// a's in one index buffer (<paramref name="aIndexCount"/> = a's index buffer length in indices), and b's node
    /// group is attached to <paramref name="node"/> (a node of a's model).
    /// </summary>
    public static MeshDesc Merge(MeshDesc a, MeshDesc b, int aIndexCount, uint? node)
    {
        var m = new MeshDesc
        {
            Header = (uint[])a.Header.Clone(),
            Records = [.. a.Records.Select(r => (byte[])r.Clone())],
            Groups = [.. a.Groups.Select(g => (byte[])g.Clone())],
            Ranges = [.. a.Ranges.Select(r => (byte[])r.Clone())],
            Streams = [.. a.Streams, .. b.Streams],
            IndexBuffer = a.IndexBuffer,
            Slots = [.. a.Slots, .. b.Slots],
            Nodes = [.. a.Nodes],
            Formats = [.. a.Formats, .. b.Formats],
            StreamSlots = [.. a.StreamSlots, .. b.StreamSlots.Select(s => (s.A, s.Stream + (uint)a.Streams.Count))],
        };
        int sub0 = a.Records.Count;
        for (int i = 0; i < b.Records.Count; i++)
        {
            var r = (byte[])b.Records[i].Clone();
            W(r, 12, U(r, 12) + (uint)aIndexCount);
            for (int k = 0; k < 6; k++) W(r, 56 + k * 4, U(r, 56 + k * 4) + (uint)a.Streams.Count);
            m.Records.Add(r);
            var rg = (byte[])b.Ranges[i].Clone();
            W(rg, 4, (uint)(sub0 + i)); W(rg, 8, U(rg, 8) + (uint)aIndexCount);
            m.Ranges.Add(rg);
            m.Nodes.Add((node ?? b.Nodes[i].Node, (uint)(sub0 + i)));
        }
        foreach (var g in b.Groups)
        {
            var c = (byte[])g.Clone();
            if (node is { } nd) W(c, 68, nd);
            W(c, 76, U(c, 76) + (uint)sub0);
            m.Groups.Add(c);
        }
        return m;
    }

    /// <summary>
    /// A copy holding only the triangles <paramref name="keep"/> accepts (submesh, and its three vertex indices
    /// including the base vertex), with a new index buffer (<paramref name="newIb"/>, 16-bit) packed submesh by
    /// submesh; submeshes left with no triangles are dropped and the node groups follow. Streams are unchanged.
    /// </summary>
    public MeshDesc Filter(byte[] ib, Func<int, int, int, int, bool> keep, out byte[] newIb)
    {
        var m = new MeshDesc
        {
            Header = (uint[])Header.Clone(), Streams = [.. Streams], IndexBuffer = IndexBuffer,
            Formats = [.. Formats], StreamSlots = [.. StreamSlots],
        };
        var outIb = new List<byte>();
        var newIndexOf = new int[Records.Count];
        for (int s = 0; s < Records.Count; s++)
        {
            newIndexOf[s] = -1;
            var (off, count) = IndexRange(s);
            int bv = (int)U(Records[s], 20), start = outIb.Count / 2;
            for (int t = 0; t + 2 < count; t += 3)
            {
                int i0 = BinaryPrimitives.ReadUInt16LittleEndian(ib.AsSpan((off + t) * 2)), i1 = BinaryPrimitives.ReadUInt16LittleEndian(ib.AsSpan((off + t + 1) * 2)),
                    i2 = BinaryPrimitives.ReadUInt16LittleEndian(ib.AsSpan((off + t + 2) * 2));
                if (!keep(s, i0 + bv, i1 + bv, i2 + bv)) continue;
                outIb.AddRange(ib.AsSpan((off + t) * 2, 6).ToArray());
            }
            int kept = outIb.Count / 2 - start;
            if (kept == 0) continue;
            newIndexOf[s] = m.Records.Count;
            var r = (byte[])Records[s].Clone(); W(r, 12, (uint)start); W(r, 16, (uint)kept);
            m.Records.Add(r);
            var rg = (byte[])Ranges[s].Clone(); W(rg, 4, (uint)newIndexOf[s]); W(rg, 8, (uint)start); W(rg, 12, (uint)kept);
            m.Ranges.Add(rg);
            m.Slots.Add(Slots[s]);
            m.Nodes.Add((Nodes[s].Node, (uint)newIndexOf[s]));
        }
        foreach (var g in Groups)
        {
            int first = (int)U(g, 76), n = (int)U(g, 80);
            var keptSubs = Enumerable.Range(first, n).Where(s => newIndexOf[s] >= 0).ToList();
            if (keptSubs.Count == 0) continue;
            var c = (byte[])g.Clone(); W(c, 76, (uint)newIndexOf[keptSubs[0]]); W(c, 80, (uint)keptSubs.Count);
            m.Groups.Add(c);
        }
        newIb = [.. outIb];
        return m;
    }

    /// <summary>Vertex positions of a submesh's stream 0 (3 floats per vertex).</summary>
    public static System.Numerics.Vector3 Position(byte[] posData, int vertex) =>
        new(BitConverter.ToSingle(posData, vertex * 12), BitConverter.ToSingle(posData, vertex * 12 + 4), BitConverter.ToSingle(posData, vertex * 12 + 8));
}

/// <summary>Material (GfxMaterialRes) parameters: u32[0] = parameter count; 36-byte records from 32 (u32[0] name
/// offset into the name table, [2] type: 3 float, 6 int, ..., [7] offset into the value block); the value block
/// follows the records.</summary>
public static class MaterialParams
{
    public static int ValueOffset(byte[] mat, string name)
    {
        int n = BitConverter.ToInt32(mat, 0), values = 32 + n * 36;
        int names = IndexOf(mat, "me_"u8, values);
        for (int k = 0; k < n; k++)
        {
            int rec = 32 + k * 36, o = names + BitConverter.ToInt32(mat, rec);
            if (o < 0 || o >= mat.Length) continue;
            int e = Array.IndexOf(mat, (byte)0, o);
            if (e > o && Encoding.ASCII.GetString(mat, o, e - o) == name) return values + BitConverter.ToInt32(mat, rec + 28);
        }
        throw new InvalidDataException($"material has no {name}");
    }

    public static float Get(byte[] mat, string name) => BitConverter.ToSingle(mat, ValueOffset(mat, name));
    public static void Set(byte[] mat, string name, float v) => BinaryPrimitives.WriteSingleLittleEndian(mat.AsSpan(ValueOffset(mat, name)), v);

    static int IndexOf(byte[] d, ReadOnlySpan<byte> pat, int from) { int i = d.AsSpan(from).IndexOf(pat); return i < 0 ? -1 : from + i; }
}
