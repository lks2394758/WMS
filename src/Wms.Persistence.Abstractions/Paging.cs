namespace Wms.Persistence.Abstractions;

/// <summary>Keyset page: at most <see cref="Limit"/> items whose sort key is greater than <see cref="After"/> (ordinal).</summary>
public sealed record PageRequest(string? After, int Limit);

/// <summary>One page. <see cref="NextAfter"/> is the sort key of the last item, or null when this is the last page.</summary>
public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextAfter);
