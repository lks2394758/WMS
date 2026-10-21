using System.Buffers.Binary;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Catalog;

/// <summary>
/// Product images: standard base64, at most 2 MiB decoded, and the bytes must structurally be a complete PNG or JPEG of
/// the declared type. Checked with the framework only (no image library): see <see cref="IsPng"/> (chunks, CRC, header
/// legality, zlib data size) and <see cref="IsJpeg"/> (markers, frame header, scan, EOI). This is a structural check,
/// not a full pixel decode: a file can pass and still render oddly. SVG, other formats, wrong content types and
/// truncated or corrupted files are rejected (<c>INVALID_IMAGE</c>). The file extension is never consulted.
/// </summary>
public static class V2ImageValidator
{
    public const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxBase64Length = (MaxBytes + 2) / 3 * 4;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Validates an optional image pair; both members or neither.</summary>
    public static V2Failure? TryRead(string? base64, string? contentType, out V2ProductImage? image)
    {
        image = null;
        if (base64 is null && contentType is null)
        {
            return null;
        }

        if (base64 is null || contentType is null)
        {
            return V2Failure.Validation(base64 is null ? "imageBase64" : "imageContentType", "imageBase64 and imageContentType go together.");
        }

        if (!ImageContentTypes.All.Contains(contentType, StringComparer.Ordinal))
        {
            return V2Failure.Validation("imageContentType", "Only image/png and image/jpeg are accepted.");
        }

        if (base64.Length == 0 || base64.Length > MaxBase64Length)
        {
            return new V2Failure(ProblemCodesV2.InvalidImage, $"The image must be 1 byte to {MaxBytes} bytes.", "imageBase64");
        }

        var buffer = new byte[base64.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out var length) || length == 0 || length > MaxBytes)
        {
            return new V2Failure(ProblemCodesV2.InvalidImage, $"imageBase64 must be standard base64 of 1 to {MaxBytes} bytes.", "imageBase64");
        }

        var bytes = buffer.AsSpan(0, length).ToArray();
        var actual = IsPng(bytes) ? ImageContentTypes.Png : IsJpeg(bytes) ? ImageContentTypes.Jpeg : null;
        if (actual is null)
        {
            return new V2Failure(ProblemCodesV2.InvalidImage, "The image is not a complete, valid PNG or JPEG file.", "imageBase64");
        }

        if (actual != contentType)
        {
            return new V2Failure(ProblemCodesV2.InvalidImage, $"The image is {actual}, not {contentType}.", "imageContentType");
        }

        image = new V2ProductImage(bytes, contentType);
        return null;
    }

    /// <summary>Largest raw (decompressed) image data accepted, as a bound against decompression bombs.</summary>
    private const long MaxRawBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Structural PNG check (not a full pixel decode): signature; chunks with valid lengths, ASCII-letter types and
    /// correct CRC-32; exactly one IHDR, first, with a non-zero size and a legal bit depth/colour type/compression/
    /// filter/interlace combination; PLTE present for palette images, forbidden for grey images, before IDAT; at least one
    /// IDAT, all consecutive; no unknown critical chunk; an empty IEND as the very last bytes. The concatenated IDAT
    /// data must be a complete zlib stream whose decompressed size is exactly the size the header implies, with a valid
    /// filter byte on every scanline of a non-interlaced image.
    /// </summary>
    internal static bool IsPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < PngSignature.Length + 12 || !data[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return false;
        }

        var offset = PngSignature.Length;
        PngHeader? header = null;
        var palette = false;
        var idatSeen = false;
        var idatEnded = false;
        using var idat = new MemoryStream();
        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > int.MaxValue || offset + 12L + length > data.Length)
            {
                return false; // truncated
            }

            var type = data.Slice(offset + 4, 4);
            var body = data.Slice(offset + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 8 + (int)length)..]);
            if (!IsChunkType(type) || Crc32(data.Slice(offset + 4, 4 + (int)length)) != crc)
            {
                return false; // corrupted
            }

            offset += 12 + (int)length;
            var isIdat = type.SequenceEqual("IDAT"u8);
            if (header is null)
            {
                if (!type.SequenceEqual("IHDR"u8) || !PngHeader.TryRead(body, out var read))
                {
                    return false; // IHDR must come first and be legal
                }

                header = read;
                continue;
            }

            if (idatSeen && !isIdat)
            {
                idatEnded = true;
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                return false; // a second IHDR
            }

            if (type.SequenceEqual("IEND"u8))
            {
                return length == 0 && offset == data.Length && idatSeen
                    && (header.Value.ColorType != 3 || palette)
                    && ImageDataMatches(idat.GetBuffer().AsSpan(0, (int)idat.Length), header.Value);
            }

            if (type.SequenceEqual("PLTE"u8))
            {
                if (palette || idatSeen || header.Value.ColorType is 0 or 4 || length == 0 || length % 3 != 0 || length > 768)
                {
                    return false;
                }

                palette = true;
            }
            else if (isIdat)
            {
                if (idatEnded)
                {
                    return false; // IDAT chunks must be consecutive
                }

                idatSeen = true;
                idat.Write(body);
            }
            else if ((type[0] & 0x20) == 0)
            {
                return false; // unknown critical chunk
            }
        }

        return false; // no IEND
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
        {
            if (b is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Decompresses the IDAT stream and checks its exact length and (non-interlaced) scanline filter bytes.</summary>
    private static bool ImageDataMatches(ReadOnlySpan<byte> compressed, PngHeader header)
    {
        var expected = header.RawSize();
        if (expected is null or > MaxRawBytes)
        {
            return false;
        }

        try
        {
            using var zlib = new System.IO.Compression.ZLibStream(
                new MemoryStream(compressed.ToArray(), writable: false), System.IO.Compression.CompressionMode.Decompress);
            var buffer = new byte[81920];
            var stride = header.Interlace == 0 ? header.RowBytes(header.Width) + 1 : 0;
            long total = 0;
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read && stride > 0; i++)
                {
                    if ((total + i) % stride == 0 && buffer[i] > 4)
                    {
                        return false; // invalid scanline filter type
                    }
                }

                total += read;
                if (total > expected)
                {
                    return false; // more data than the header implies
                }
            }

            return total == expected;
        }
        catch (InvalidDataException)
        {
            return false; // not a complete, valid zlib stream
        }
    }

    private readonly record struct PngHeader(long Width, long Height, int BitDepth, int ColorType, int Interlace)
    {
        public static bool TryRead(ReadOnlySpan<byte> body, out PngHeader header)
        {
            header = default;
            if (body.Length != 13)
            {
                return false;
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(body);
            var height = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            int depth = body[8], color = body[9], compression = body[10], filter = body[11], interlace = body[12];
            var legalDepth = color switch
            {
                0 => depth is 1 or 2 or 4 or 8 or 16,
                3 => depth is 1 or 2 or 4 or 8,
                2 or 4 or 6 => depth is 8 or 16,
                _ => false,
            };
            if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue || !legalDepth
                || compression != 0 || filter != 0 || interlace > 1)
            {
                return false;
            }

            header = new PngHeader(width, height, depth, color, interlace);
            return true;
        }

        private int Channels => ColorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };

        public long RowBytes(long width) => ((width * Channels * BitDepth) + 7) / 8;

        /// <summary>Exact filtered data size (scanlines with filter bytes), Adam7 passes when interlaced; null if absurd.</summary>
        public long? RawSize()
        {
            if (Interlace == 0)
            {
                return Checked(Height, RowBytes(Width) + 1);
            }

            ReadOnlySpan<int> xStart = [0, 4, 0, 2, 0, 1, 0];
            ReadOnlySpan<int> yStart = [0, 0, 4, 0, 2, 0, 1];
            ReadOnlySpan<int> xStep = [8, 8, 4, 4, 2, 2, 1];
            ReadOnlySpan<int> yStep = [8, 8, 8, 4, 4, 2, 2];
            long total = 0;
            for (var pass = 0; pass < 7; pass++)
            {
                var w = (Width - xStart[pass] + xStep[pass] - 1) / xStep[pass];
                var h = (Height - yStart[pass] + yStep[pass] - 1) / yStep[pass];
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                var size = Checked(h, RowBytes(w) + 1);
                if (size is null)
                {
                    return null;
                }

                total += size.Value;
                if (total > MaxRawBytes)
                {
                    return null;
                }
            }

            return total;
        }

        private static long? Checked(long a, long b) => a > 0 && b > MaxRawBytes / a ? null : a * b;
    }

    internal static bool IsJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8 || data[^2] != 0xFF || data[^1] != 0xD9)
        {
            return false;
        }

        var offset = 2;
        var frame = false;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                return false;
            }

            var marker = data[offset + 1];
            if (marker == 0xFF)
            {
                offset++; // fill byte
                continue;
            }

            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7) or 0x01)
            {
                return false; // no second SOI, EOI or RST before the scan
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (length < 2 || offset + 2 + length > data.Length)
            {
                return false; // truncated segment
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 8 || BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]) == 0
                    || BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 7)..]) == 0)
                {
                    return false; // frame without size
                }

                frame = true;
            }

            if (marker == 0xDA)
            {
                // Start of scan: entropy-coded data follows; the file must still end with EOI (checked above) and have
                // at least one byte of scan data before it.
                return frame && offset + 2 + length < data.Length - 2;
            }

            offset += 2 + length;
        }

        return false; // no scan
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
