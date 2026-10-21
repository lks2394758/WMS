using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>Builds small, real image files (and broken variants) for the image validation tests.</summary>
internal static class TestImages
{
    public static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A valid 1x1 RGBA PNG: IHDR, one IDAT (zlib, filter byte 0 + one pixel), empty IEND, correct CRCs.</summary>
    public static byte[] Png() => Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IDAT", Zlib([0, 255, 0, 0, 255])), Chunk("IEND", []));

    public static byte[] Ihdr(uint width, uint height, byte depth, byte color, byte interlace = 0)
    {
        var body = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(body, width);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), height);
        body[8] = depth;
        body[9] = color;
        body[12] = interlace;
        return Chunk("IHDR", body);
    }

    public static byte[] Chunk(string type, byte[] data, bool corruptCrc = false)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = Crc32(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), corruptCrc ? crc ^ 1u : crc);
        return chunk;
    }

    public static byte[] Zlib(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    /// <summary>A structurally valid baseline JPEG skeleton: SOI, SOF0 (1x1, 3 components), SOS, one scan byte, EOI.</summary>
    public static byte[] Jpeg() =>
    [
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
        0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00,
        0x00,
        0xFF, 0xD9,
    ];

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
