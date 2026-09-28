using System.Buffers.Binary;
using System.Text;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// Builds raw J2K codestreams and JP2 wrappers with arbitrary — including illegal — header fields,
/// for <see cref="JpxMalformedInputTests"/>. Every field defaults to a small, conformant 16×16
/// single-component 5/3 stream with an empty tile-part body; a test sets the one or two fields it
/// wants wrong. Deliberately not the fixture generator: the point is to reach header states no
/// encoder will emit (a 32-level decomposition on a 16-pixel image, a 4096²-precinct partition, a
/// multiple component transform over sub-sampled components, a <c>Psot</c> past EOF), each of
/// which is a few dozen bytes. Adapted from a review fuzz harness.
/// </summary>
internal sealed class J2kBuilder
{
    // SIZ (A.5.1)
    public int Rsiz { get; set; }

    public long Xsiz { get; set; } = 16;

    public long Ysiz { get; set; } = 16;

    public long XOsiz { get; set; }

    public long YOsiz { get; set; }

    public long XTsiz { get; set; } = 16;

    public long YTsiz { get; set; } = 16;

    public long XTOsiz { get; set; }

    public long YTOsiz { get; set; }

    /// <summary>Component count when <see cref="Components"/> is not set (each 8-bit unsigned, 1×1).</summary>
    public int Csiz { get; set; } = 1;

    /// <summary>Per-component (<c>Ssiz</c>, <c>XRsiz</c>, <c>YRsiz</c>); overrides <see cref="Csiz"/>.</summary>
    public (int Ssiz, int XRsiz, int YRsiz)[]? Components { get; set; }

    /// <summary>Writes this <c>Csiz</c> field regardless of the component list actually emitted (−1 = consistent).</summary>
    public int DeclaredCsiz { get; set; } = -1;

    /// <summary>Overrides <c>Lsiz</c> (−1 = the correct length).</summary>
    public int LsizOverride { get; set; } = -1;

    // COD (A.6.1)
    public int Scod { get; set; }

    public int Progression { get; set; }

    public int Layers { get; set; } = 1;

    public int Mct { get; set; }

    /// <summary><c>N_L</c>.</summary>
    public int DecompositionLevels { get; set; } = 1;

    public int XcbMinus2 { get; set; } = 4;

    public int YcbMinus2 { get; set; } = 4;

    public int CodeBlockStyle { get; set; }

    /// <summary>1 = 5/3 reversible (default), 0 = 9/7 irreversible.</summary>
    public int Transform { get; set; } = 1;

    /// <summary>One <c>PPy&lt;&lt;4 | PPx</c> byte per resolution when <see cref="Scod"/> bit 0 is set; defaults to all zero.</summary>
    public int[]? PrecinctBytes { get; set; }

    // QCD (A.6.4)
    /// <summary><c>Sqcd</c>: default 0x40 = two guard bits, style 0 (no quantisation).</summary>
    public int Sqcd { get; set; } = 0x40;

    /// <summary>Number of <c>SPqcd</c> bytes (−1 = <c>3·N_L + 1</c> single-byte style-0 entries); every byte is <see cref="QcdStepByte"/>.</summary>
    public int QcdStepBytes { get; set; } = -1;

    public byte QcdStepByte { get; set; } = 0x40;

    /// <summary>Extra marker segments (complete, marker included) inserted after QCD in the main header.</summary>
    public List<byte[]> ExtraMainHeaderSegments { get; } = new();

    // tile-part (A.4.2)
    /// <summary><c>Psot</c> (−1 = computed from the actual tile-part length).</summary>
    public long Psot { get; set; } = -1;

    public int Isot { get; set; }

    public int TPsot { get; set; }

    public int TNsot { get; set; } = 1;

    /// <summary>Packet bytes after <c>SOD</c>; empty by default (every packet header then runs out of bits immediately).</summary>
    public byte[] Body { get; set; } = [];

    public bool EmitSot { get; set; } = true;

    public bool EmitEoc { get; set; } = true;

    public byte[] BuildCodestream()
    {
        var ms = new MemoryStream();
        WriteU16(ms, 0xFF4F);
        WriteSiz(ms);
        WriteCod(ms);
        WriteQcd(ms);
        foreach (var segment in ExtraMainHeaderSegments)
        {
            ms.Write(segment);
        }

        if (EmitSot)
        {
            ms.Write(TilePart(Isot, TPsot, TNsot, Body, Psot));
        }

        if (EmitEoc)
        {
            WriteU16(ms, 0xFFD9);
        }

        return ms.ToArray();
    }

    /// <summary>The main header alone (SOC..QCD + extras), for streams that append their own tile-parts.</summary>
    public byte[] BuildMainHeader()
    {
        var emitSot = EmitSot;
        var emitEoc = EmitEoc;
        EmitSot = false;
        EmitEoc = false;
        try
        {
            return BuildCodestream();
        }
        finally
        {
            EmitSot = emitSot;
            EmitEoc = emitEoc;
        }
    }

    /// <summary>One complete tile-part: <c>SOT</c> (with <paramref name="psot"/>, or the true length when −1), <c>SOD</c>, <paramref name="body"/>.</summary>
    public static byte[] TilePart(int isot, int tpsot, int tnsot, byte[] body, long psot = -1)
    {
        var ms = new MemoryStream();
        WriteU16(ms, 0xFF90);
        WriteU16(ms, 10);
        WriteU16(ms, isot);
        var psotPosition = (int)ms.Length;
        WriteU32(ms, 0);
        ms.WriteByte((byte)tpsot);
        ms.WriteByte((byte)tnsot);
        WriteU16(ms, 0xFF93);
        ms.Write(body);
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(psotPosition), psot >= 0 ? (uint)psot : (uint)bytes.Length);
        return bytes;
    }

    /// <summary>A <c>POC</c> marker segment (A.6.6) with one-byte component fields, one entry per tuple (<c>RSpoc</c>, <c>CSpoc</c>, <c>LYEpoc</c>, <c>REpoc</c>, <c>CEpoc</c>, <c>Ppoc</c>).</summary>
    public static byte[] Poc(params (int RSpoc, int CSpoc, int LYEpoc, int REpoc, int CEpoc, int Ppoc)[] entries)
    {
        var ms = new MemoryStream();
        WriteU16(ms, 0xFF5F);
        WriteU16(ms, 2 + (7 * entries.Length));
        foreach (var (rs, cs, ly, re, ce, p) in entries)
        {
            ms.WriteByte((byte)rs);
            ms.WriteByte((byte)cs);
            WriteU16(ms, ly);
            ms.WriteByte((byte)re);
            ms.WriteByte((byte)ce);
            ms.WriteByte((byte)p);
        }

        return ms.ToArray();
    }

    // ---- JP2 wrapper helpers (Annex I) ----

    public static byte[] Box(string type, byte[] body)
    {
        var ms = new MemoryStream();
        WriteU32(ms, (uint)(8 + body.Length));
        ms.Write(Encoding.ASCII.GetBytes(type));
        ms.Write(body);
        return ms.ToArray();
    }

    /// <summary>Wraps <paramref name="codestream"/> in a JP2 container (signature, <c>ftyp</c>, <c>jp2h</c> with the given children, <c>jp2c</c>).</summary>
    public static byte[] Jp2(byte[] codestream, params byte[][] jp2hChildren)
    {
        var ms = new MemoryStream();
        ms.Write([0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A]);
        ms.Write(Box("ftyp", [0x6A, 0x70, 0x32, 0x20, 0, 0, 0, 0, 0x6A, 0x70, 0x32, 0x20]));
        var header = new MemoryStream();
        foreach (var child in jp2hChildren)
        {
            header.Write(child);
        }

        ms.Write(Box("jp2h", header.ToArray()));
        ms.Write(Box("jp2c", codestream));
        return ms.ToArray();
    }

    public static byte[] Ihdr(int height, int width, int componentCount, int bpc)
    {
        var ms = new MemoryStream();
        WriteU32(ms, (uint)height);
        WriteU32(ms, (uint)width);
        WriteU16(ms, componentCount);
        ms.WriteByte((byte)bpc);
        ms.WriteByte(7);
        ms.WriteByte(0);
        ms.WriteByte(0);
        return Box("ihdr", ms.ToArray());
    }

    /// <summary>A <c>pclr</c> box with <paramref name="entries"/> entries of <paramref name="columns"/> columns, every column <paramref name="depthMinus1"/> + 1 bits deep.</summary>
    public static byte[] Pclr(int entries, int columns, int depthMinus1)
    {
        var ms = new MemoryStream();
        WriteU16(ms, entries);
        ms.WriteByte((byte)columns);
        for (var c = 0; c < columns; c++)
        {
            ms.WriteByte((byte)depthMinus1);
        }

        var bytesPer = ((depthMinus1 & 0x7F) + 1 + 7) / 8;
        for (var e = 0; e < entries; e++)
        {
            for (var c = 0; c < columns; c++)
            {
                for (var b = 0; b < bytesPer; b++)
                {
                    ms.WriteByte((byte)(e ^ c));
                }
            }
        }

        return Box("pclr", ms.ToArray());
    }

    /// <summary>A <c>cmap</c> box of <paramref name="count"/> entries, each produced by <paramref name="entry"/> as (<c>CMP</c>, <c>MTYP</c>, <c>PCOL</c>).</summary>
    public static byte[] Cmap(int count, Func<int, (int Cmp, int Mtyp, int Pcol)> entry)
    {
        var ms = new MemoryStream();
        for (var i = 0; i < count; i++)
        {
            var (cmp, mtyp, pcol) = entry(i);
            WriteU16(ms, cmp);
            ms.WriteByte((byte)mtyp);
            ms.WriteByte((byte)pcol);
        }

        return Box("cmap", ms.ToArray());
    }

    public static byte[] Cdef(params (int Channel, int Typ, int Assoc)[] entries)
    {
        var ms = new MemoryStream();
        WriteU16(ms, entries.Length);
        foreach (var (channel, typ, assoc) in entries)
        {
            WriteU16(ms, channel);
            WriteU16(ms, typ);
            WriteU16(ms, assoc);
        }

        return Box("cdef", ms.ToArray());
    }

    public static byte[] ColrEnum(int enumCs)
    {
        var ms = new MemoryStream();
        ms.WriteByte(1);
        ms.WriteByte(0);
        ms.WriteByte(0);
        WriteU32(ms, (uint)enumCs);
        return Box("colr", ms.ToArray());
    }

    public static void WriteU16(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    public static void WriteU32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private void WriteSiz(MemoryStream ms)
    {
        var components = Components ?? Enumerable.Range(0, Csiz).Select(_ => (7, 1, 1)).ToArray();
        var declared = DeclaredCsiz >= 0 ? DeclaredCsiz : components.Length;
        WriteU16(ms, 0xFF51);
        WriteU16(ms, LsizOverride >= 0 ? LsizOverride : 38 + (3 * components.Length));
        WriteU16(ms, Rsiz);
        WriteU32(ms, (uint)Xsiz);
        WriteU32(ms, (uint)Ysiz);
        WriteU32(ms, (uint)XOsiz);
        WriteU32(ms, (uint)YOsiz);
        WriteU32(ms, (uint)XTsiz);
        WriteU32(ms, (uint)YTsiz);
        WriteU32(ms, (uint)XTOsiz);
        WriteU32(ms, (uint)YTOsiz);
        WriteU16(ms, declared);
        foreach (var (ssiz, xr, yr) in components)
        {
            ms.WriteByte((byte)ssiz);
            ms.WriteByte((byte)xr);
            ms.WriteByte((byte)yr);
        }
    }

    private void WriteCod(MemoryStream ms)
    {
        var precincts = (Scod & 1) != 0 ? PrecinctBytes ?? new int[DecompositionLevels + 1] : [];
        WriteU16(ms, 0xFF52);
        WriteU16(ms, 12 + precincts.Length);
        ms.WriteByte((byte)Scod);
        ms.WriteByte((byte)Progression);
        WriteU16(ms, Layers);
        ms.WriteByte((byte)Mct);
        ms.WriteByte((byte)DecompositionLevels);
        ms.WriteByte((byte)XcbMinus2);
        ms.WriteByte((byte)YcbMinus2);
        ms.WriteByte((byte)CodeBlockStyle);
        ms.WriteByte((byte)Transform);
        foreach (var p in precincts)
        {
            ms.WriteByte((byte)p);
        }
    }

    private void WriteQcd(MemoryStream ms)
    {
        var count = QcdStepBytes >= 0 ? QcdStepBytes : (3 * DecompositionLevels) + 1;
        WriteU16(ms, 0xFF5C);
        WriteU16(ms, 3 + count);
        ms.WriteByte((byte)Sqcd);
        for (var i = 0; i < count; i++)
        {
            ms.WriteByte(QcdStepByte);
        }
    }
}
