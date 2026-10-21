namespace Wms.Contracts.V2.Common;

/// <summary>One page of a v2 list. Pass <see cref="NextCursor"/> back as <c>?cursor=</c>; absent on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor = null);

/// <summary>
/// Optional body of <c>DELETE</c> (archive) requests. When <see cref="Version"/> is sent it must equal the current
/// version, otherwise <c>409 VERSION_CONFLICT</c>; an empty body <c>{}</c> or no body skips the check.
/// </summary>
public sealed record DeleteRequest(int? Version = null);

/// <summary>A reference to a business object, e.g. <c>{ "type": "countTask", "id": "12", "label": "CT-000012" }</c>.</summary>
public sealed record SubjectRef(string Type, string Id, string? Label = null);

/// <summary>
/// v2 <c>application/problem+json</c>: the v1 shape plus optional <see cref="Subjects"/> naming the objects the
/// error is about (e.g. the task freezing a bin). Clients branch on <see cref="Code"/> only.
/// </summary>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Code,
    string TraceId,
    string? Detail = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    IReadOnlyList<SubjectRef>? Subjects = null);
