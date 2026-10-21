using System.Buffers.Text;
using System.Text;
using Wms.Contracts.Common;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Common;

/// <summary>Cursor pagination (docs/api-conventions.md). The cursor is the opaque, base64url-encoded page key.</summary>
public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    private const int MaxCursorLength = 512;

    public static Result<PageRequest> Parse(string? cursor, int? limit)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return DomainErrors.Validation($"limit must be between 1 and {MaxLimit}.", "limit");
        }

        if (string.IsNullOrEmpty(cursor))
        {
            return new PageRequest(null, size);
        }

        if (cursor.Length > MaxCursorLength)
        {
            return DomainErrors.Validation("cursor is not valid.", "cursor");
        }

        try
        {
            return new PageRequest(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)), size);
        }
        catch (FormatException)
        {
            return DomainErrors.Validation("cursor is not valid.", "cursor");
        }
    }

    public static CursorPage<TOut> ToPage<TIn, TOut>(PageResult<TIn> page, Func<TIn, TOut> map) =>
        new(
            page.Items.Select(map).ToList(),
            page.NextAfter is null ? null : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(page.NextAfter)));
}
