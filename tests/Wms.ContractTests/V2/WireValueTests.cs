using Wms.Contracts.V2.Common;

namespace Wms.ContractTests.V2;

public class WireValueTests
{
    [Theory]
    [InlineData("1", 1L)]
    [InlineData("42", 42L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Valid_ids_parse(string text, long expected)
    {
        Assert.True(WireId.TryParse(text, out var id));
        Assert.Equal(expected, id);
        Assert.Equal(text, WireId.Format(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("007")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]
    [InlineData("12345678901234567890")]
    [InlineData("٣")]
    [InlineData("9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c05")]
    public void Invalid_ids_are_rejected(string? text)
    {
        Assert.False(WireId.IsValid(text));
    }

    [Fact]
    public void Formatting_a_non_positive_id_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WireId.Format(0));
    }

    [Theory]
    [InlineData("1", QuantitySign.Positive, 1_000_000L)]
    [InlineData("0.000001", QuantitySign.Positive, 1L)]
    [InlineData("12.5", QuantitySign.Positive, 12_500_000L)]
    [InlineData("01.50", QuantitySign.Positive, 1_500_000L)]
    [InlineData("999999999999.999999", QuantitySign.Positive, WireQuantity.MaxMicros)]
    [InlineData("0", QuantitySign.NonNegative, 0L)]
    [InlineData("0.0", QuantitySign.NonNegative, 0L)]
    [InlineData("-3.25", QuantitySign.NonZeroSigned, -3_250_000L)]
    [InlineData("3", QuantitySign.NonZeroSigned, 3_000_000L)]
    public void Valid_quantities_parse_exactly(string text, QuantitySign sign, long expectedMicros)
    {
        Assert.True(WireQuantity.TryParse(text, sign, out var micros));
        Assert.Equal(expectedMicros, micros);
    }

    [Theory]
    [InlineData(null, QuantitySign.NonNegative)]
    [InlineData("", QuantitySign.NonNegative)]
    [InlineData("0", QuantitySign.Positive)]
    [InlineData("0", QuantitySign.NonZeroSigned)]
    [InlineData("-0", QuantitySign.NonZeroSigned)]
    [InlineData("-1", QuantitySign.Positive)]
    [InlineData("-1", QuantitySign.NonNegative)]
    [InlineData("+1", QuantitySign.Positive)]
    [InlineData("1.", QuantitySign.Positive)]
    [InlineData(".5", QuantitySign.Positive)]
    [InlineData("1.0000001", QuantitySign.Positive)]
    [InlineData("1e3", QuantitySign.Positive)]
    [InlineData("1,000", QuantitySign.Positive)]
    [InlineData(" 1", QuantitySign.Positive)]
    [InlineData("1 ", QuantitySign.Positive)]
    [InlineData("NaN", QuantitySign.Positive)]
    [InlineData("1000000000000", QuantitySign.Positive)]
    [InlineData("-1000000000000", QuantitySign.NonZeroSigned)]
    [InlineData("1.2.3", QuantitySign.Positive)]
    public void Invalid_quantities_are_rejected(string? text, QuantitySign sign)
    {
        Assert.False(WireQuantity.TryParse(text, sign, out _));
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(1L, "0.000001")]
    [InlineData(1_500_000L, "1.5")]
    [InlineData(-3_250_000L, "-3.25")]
    [InlineData(WireQuantity.MaxMicros, WireQuantity.MaxText)]
    [InlineData(long.MaxValue, "9223372036854.775807")]
    [InlineData(long.MinValue, "-9223372036854.775808")]
    public void Quantities_format_canonically(long micros, string expected)
    {
        Assert.Equal(expected, WireQuantity.Format(micros));
    }

    [Theory]
    [InlineData("1.5", true)]
    [InlineData("0", true)]
    [InlineData("-2", true)]
    [InlineData("1.50", false)]
    [InlineData("01", false)]
    [InlineData("-0", false)]
    public void Canonical_form_is_detected(string text, bool expected)
    {
        Assert.Equal(expected, WireQuantity.IsCanonical(text));
    }

    [Theory]
    [InlineData(5_000_000L, 0)]
    [InlineData(1_500_000L, 1)]
    [InlineData(-1_230_000L, 2)]
    [InlineData(1L, 6)]
    public void Fraction_digits_measure_the_value_not_the_typed_text(long micros, int expected)
    {
        Assert.Equal(expected, WireQuantity.FractionDigits(micros));
    }
}
