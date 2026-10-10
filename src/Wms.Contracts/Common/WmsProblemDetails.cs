namespace Wms.Contracts.Common;

/// <summary>
/// RFC 9457 problem details as returned with <c>application/problem+json</c>, extended with a stable
/// <see cref="Code"/> and the request <see cref="TraceId"/>. <see cref="Errors"/> is present only for validation failures.
/// </summary>
public sealed record WmsProblemDetails(
    string Type,
    string Title,
    int Status,
    string Code,
    string TraceId,
    string? Detail = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
