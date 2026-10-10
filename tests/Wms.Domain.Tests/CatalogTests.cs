using Wms.Domain.Catalog;
using Wms.Domain.Common;

namespace Wms.Domain.Tests;

public class CatalogTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Capacity_must_be_positive_when_set(int capacity)
    {
        var result = Product.Create(ProductId.New(), "SKU-1", "Widget", "EA", capacity);

        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Fact]
    public void Capacity_can_be_cleared()
    {
        var product = Product.Create(ProductId.New(), "SKU-1", "Widget", "EA", 50).Value;

        Assert.True(product.SetCapacity(null).IsSuccess);
        Assert.Null(product.Capacity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sku_is_required(string sku)
    {
        Assert.True(Product.Create(ProductId.New(), sku, "Widget").IsFailure);
    }

    [Fact]
    public void Location_code_is_trimmed_and_upper_cased()
    {
        var location = Location.Create(LocationId.New(), WarehouseId.New(), " a-01-01 ").Value;

        Assert.Equal("A-01-01", location.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345\t678")]
    public void Invalid_barcodes_are_rejected(string value)
    {
        Assert.True(Barcode.Create(value, ProductId.New()).IsFailure);
    }
}
