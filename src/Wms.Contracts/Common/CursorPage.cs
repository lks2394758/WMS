namespace Wms.Contracts.Common;

/// <summary>One page of a list. Pass <see cref="NextCursor"/> back as <c>?cursor=</c>; null means the last page.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
