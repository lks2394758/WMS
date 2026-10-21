using System.Text;
using Wms.Application.V2.Catalog;
using Wms.Contracts.V2.Common;
using static Wms.Persistence.Sqlite.Tests.V2.TestImages;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>Structural image checks (not a pixel decode): real PNG/JPEG accepted, broken or disguised files rejected.</summary>
public class V2ImageValidatorTests
{
    [Fact]
    public void A_real_1x1_rgba_png_and_a_jpeg_skeleton_are_accepted()
    {
        Assert.Null(V2ImageValidator.TryRead(Base64(Png()), "image/png", out var png));
        Assert.Equal(Png(), png!.Content);
        Assert.Null(V2ImageValidator.TryRead(Base64(Jpeg()), "image/jpeg", out var jpeg));
        Assert.Equal("image/jpeg", jpeg!.ContentType);
    }

    public static TheoryData<string, byte[]> BrokenPngs => new()
    {
        { "no IDAT", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IEND", [])) },
        { "non-empty IEND", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IDAT", Zlib([0, 1, 2, 3, 4])), Chunk("IEND", [1])) },
        { "duplicate IHDR", Concat(Signature, Ihdr(1, 1, 8, 6), Ihdr(1, 1, 8, 6), Chunk("IDAT", Zlib([0, 1, 2, 3, 4])), Chunk("IEND", [])) },
        { "illegal depth for RGBA", Concat(Signature, Ihdr(1, 1, 4, 6), Chunk("IDAT", Zlib([0, 1])), Chunk("IEND", [])) },
        { "illegal colour type", Concat(Signature, Ihdr(1, 1, 8, 5), Chunk("IDAT", Zlib([0, 1, 2, 3, 4])), Chunk("IEND", [])) },
        { "zero width", Concat(Signature, Ihdr(0, 1, 8, 6), Chunk("IDAT", Zlib([0])), Chunk("IEND", [])) },
        { "illegal interlace", Concat(Signature, Ihdr(1, 1, 8, 6, interlace: 2), Chunk("IDAT", Zlib([0, 1, 2, 3, 4])), Chunk("IEND", [])) },
        { "wrong IDAT CRC", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IDAT", Zlib([0, 1, 2, 3, 4]), corruptCrc: true), Chunk("IEND", [])) },
        { "IDAT not zlib", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IDAT", [1, 2, 3, 4, 5]), Chunk("IEND", [])) },
        { "too little pixel data", Concat(Signature, Ihdr(2, 2, 8, 6), Chunk("IDAT", Zlib([0, 1, 2, 3, 4])), Chunk("IEND", [])) },
        { "too much pixel data", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IDAT", Zlib([0, 1, 2, 3, 4, 5])), Chunk("IEND", [])) },
        { "invalid filter type", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IDAT", Zlib([9, 1, 2, 3, 4])), Chunk("IEND", [])) },
        { "palette image without PLTE", Concat(Signature, Ihdr(1, 1, 8, 3), Chunk("IDAT", Zlib([0, 0])), Chunk("IEND", [])) },
        { "split IDAT", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("IDAT", Zlib([0, 1, 2, 3, 4])), Chunk("tEXt", [65]), Chunk("IDAT", []), Chunk("IEND", [])) },
        { "unknown critical chunk", Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("ABCD", [1]), Chunk("IDAT", Zlib([0, 1, 2, 3, 4])), Chunk("IEND", [])) },
        { "bytes after IEND", Concat(Png(), [0]) },
        { "truncated", Png()[..^5] },
        { "signature only", Signature },
    };

    [Theory]
    [MemberData(nameof(BrokenPngs))]
    public void Broken_pngs_are_rejected(string why, byte[] bytes)
    {
        var failure = V2ImageValidator.TryRead(Base64(bytes), "image/png", out var image);

        Assert.True(failure is not null, why);
        Assert.Equal(ProblemCodesV2.InvalidImage, failure!.Code);
        Assert.Null(image);
    }

    [Fact]
    public void A_correct_crc_alone_does_not_make_a_valid_png()
    {
        // Every chunk below has a correct CRC; the image is still structurally invalid (no IDAT).
        var bytes = Concat(Signature, Ihdr(1, 1, 8, 6), Chunk("tEXt", Encoding.ASCII.GetBytes("k\0v")), Chunk("IEND", []));

        Assert.Equal(ProblemCodesV2.InvalidImage, V2ImageValidator.TryRead(Base64(bytes), "image/png", out _)!.Code);
    }

    [Fact]
    public void Disguised_truncated_and_mislabelled_files_are_rejected()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        Assert.Equal(ProblemCodesV2.InvalidImage, V2ImageValidator.TryRead(Base64(svg), "image/png", out _)!.Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, V2ImageValidator.TryRead(Base64(svg), "image/svg+xml", out _)!.Code);
        Assert.Equal(ProblemCodesV2.InvalidImage, V2ImageValidator.TryRead(Base64(Png()), "image/jpeg", out _)!.Code);
        Assert.Equal(ProblemCodesV2.InvalidImage, V2ImageValidator.TryRead(Base64(Jpeg()[..^2]), "image/jpeg", out _)!.Code);
        Assert.Equal(ProblemCodesV2.InvalidImage, V2ImageValidator.TryRead("not base64!", "image/png", out _)!.Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, V2ImageValidator.TryRead(Base64(Png()), null, out _)!.Code);
    }

    [Fact]
    public void Images_over_2_mib_are_rejected_before_decoding()
    {
        var tooLarge = new byte[V2ImageValidator.MaxBytes + 1];
        Signature.CopyTo(tooLarge, 0);

        Assert.Equal(ProblemCodesV2.InvalidImage, V2ImageValidator.TryRead(Base64(tooLarge), "image/png", out _)!.Code);
    }
}
