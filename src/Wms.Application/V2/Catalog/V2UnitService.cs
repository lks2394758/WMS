using Wms.Application.V2.Auth;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Catalog;

/// <summary>
/// Units (decisions §2, §17.4): name trimmed, 1-32 characters, unique case-insensitively (Unicode invariant upper case).
/// Renaming only corrects the name; history keeps its snapshots. The preset unit and any unit referenced by a product
/// (archived included) cannot be deleted.
/// </summary>
public sealed class V2UnitService(IV2CatalogReader reader, V2CatalogWriter writer)
{
    private const int MaxName = 32;

    public async Task<V2Result<Page<UnitResponse>>> ListAsync(string? search, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var after, out var size) is { } invalid)
        {
            return invalid;
        }

        var normalized = string.IsNullOrWhiteSpace(search) ? null : V2Text.Normalize(search);
        var rows = await reader.ListUnitsAsync(normalized, after, size + 1, cancellationToken);
        var page = rows.Take(size).Select(V2CatalogMapping.ToResponse).ToList();
        return new Page<UnitResponse>(page, rows.Count > size ? V2Paging.Encode(rows[size - 1].Id) : null);
    }

    public async Task<V2Result<UnitResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var unitId) is { } invalid)
        {
            return invalid;
        }

        var unit = await reader.FindUnitAsync(unitId, cancellationToken);
        if (unit is null)
        {
            return V2Failure.NotFound("Unit");
        }

        return V2CatalogMapping.ToResponse(unit);
    }

    public async Task<V2Result<UnitResponse>> CreateAsync(V2SessionPrincipal principal, UnitWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (V2Text.Name(request.Name, MaxName, "name", out var name) is { } invalid)
        {
            return await writer.RejectAsync<UnitResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.UnitCreate, "新增单位", [], invalid, cancellationToken);
        }

        var normalized = V2Text.Normalize(name);
        return await writer.RunAsync<UnitResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.UnitCreate, $"新增单位 {name}", [],
            async (scope, actor, now) =>
            {
                if (await scope.FindUnitByNormalizedNameAsync(normalized, cancellationToken) is { } existing)
                {
                    return AlreadyExists(existing);
                }

                var id = await scope.InsertUnitAsync(name, normalized, now, cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.UnitCreate, $"新增单位 {name}", true, [new(SubjectTypes.Unit, id, name)],
                    actor.UserId, actor.DisplayName, actor.Role));
                return V2CatalogMapping.ToResponse((await scope.FindUnitAsync(id, cancellationToken))!);
            },
            cancellationToken);
    }

    public async Task<V2Result<UnitResponse>> UpdateAsync(
        V2SessionPrincipal principal, string id, UnitWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var unitId);
        var badName = V2Text.Name(request.Name, MaxName, "name", out var name);
        var rejected = badId ?? badName
            ?? (request.Version is null ? V2Failure.Validation("version", "version is required to update a unit.") : null);
        if (rejected is not null)
        {
            return await writer.RejectAsync<UnitResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.UnitUpdate, "修改单位", Subjects(unitId), rejected, cancellationToken);
        }

        var version = request.Version!.Value;

        var normalized = V2Text.Normalize(name);
        var subject = V2CatalogMapping.Ref(SubjectTypes.Unit, unitId);
        return await writer.RunAsync<UnitResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.UnitUpdate, $"修改单位 {name}", [new(SubjectTypes.Unit, unitId)],
            async (scope, actor, now) =>
            {
                var unit = await scope.FindUnitAsync(unitId, cancellationToken);
                if (unit is null)
                {
                    return V2Failure.NotFound("Unit", subject);
                }

                if (unit.Version != version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (await scope.FindUnitByNormalizedNameAsync(normalized, cancellationToken) is { } other && other.Id != unitId)
                {
                    return AlreadyExists(other);
                }

                if (!await scope.UpdateUnitAsync(unitId, version, name, normalized, now, cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.UnitUpdate, $"修改单位 {unit.Name} → {name}", true, [new(SubjectTypes.Unit, unitId, name)],
                    actor.UserId, actor.DisplayName, actor.Role, Changes: [new("name", unit.Name, name)]));
                return V2CatalogMapping.ToResponse((await scope.FindUnitAsync(unitId, cancellationToken))!);
            },
            cancellationToken);
    }

    public async Task<V2Result<V2None>> DeleteAsync(
        V2SessionPrincipal principal, string id, DeleteRequest? request, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var unitId) is { } badId)
        {
            return await writer.RejectAsync<V2None>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.UnitDelete, "删除单位", [], badId, cancellationToken);
        }

        var subject = V2CatalogMapping.Ref(SubjectTypes.Unit, unitId);
        return await writer.RunAsync<V2None>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.UnitDelete, "删除单位", [new(SubjectTypes.Unit, unitId)],
            async (scope, actor, now) =>
            {
                var unit = await scope.FindUnitAsync(unitId, cancellationToken);
                if (unit is null)
                {
                    return V2Failure.NotFound("Unit", subject);
                }

                if (request?.Version is { } version && version != unit.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (unit.IsSystem)
                {
                    return new V2Failure(ProblemCodesV2.InUse, "The preset unit cannot be deleted.", Subjects: [subject]);
                }

                if (unit.ProductCount > 0)
                {
                    return new V2Failure(
                        ProblemCodesV2.InUse,
                        $"The unit is used by {unit.ProductCount} product(s), archived ones included.",
                        Subjects: [subject]);
                }

                await scope.DeleteUnitAsync(unitId, cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.UnitDelete, $"删除单位 {unit.Name}", true, [new(SubjectTypes.Unit, unitId, unit.Name)],
                    actor.UserId, actor.DisplayName, actor.Role, Changes: [new("name", unit.Name, null)]));
                return V2None.Value;
            },
            cancellationToken);
    }

    private static IReadOnlyList<V2SubjectRecord> Subjects(long unitId) => unitId > 0 ? [new(SubjectTypes.Unit, unitId)] : [];

    private static V2Failure AlreadyExists(V2UnitRecord existing) =>
        new(ProblemCodesV2.AlreadyExists, $"A unit named '{existing.Name}' already exists.", "name",
            [V2CatalogMapping.Ref(SubjectTypes.Unit, existing.Id, existing.Name)]);
}
