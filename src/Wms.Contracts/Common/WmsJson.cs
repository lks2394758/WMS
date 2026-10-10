using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wms.Contracts.Common;

/// <summary>
/// The one serializer configuration for the public contract (guide §4.2): camelCase properties, nulls omitted,
/// times written as ISO 8601 UTC with a <c>Z</c> suffix. API, Worker and tests all use it.
/// </summary>
public static class WmsJson
{
    public static JsonSerializerOptions Options { get; } = Apply(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Applies the contract settings to an existing options instance (e.g. ASP.NET Core's HTTP JSON options).</summary>
    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        if (!options.Converters.OfType<UtcDateTimeOffsetJsonConverter>().Any())
        {
            options.Converters.Add(new UtcDateTimeOffsetJsonConverter());
        }

        return options;
    }
}

/// <summary>Reads any ISO 8601 timestamp; always writes UTC, e.g. <c>2026-10-10T18:00:00Z</c>.</summary>
public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
