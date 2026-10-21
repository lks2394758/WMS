using Wms.Application.V2.Auth;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Catalog;

/// <summary>
/// Category tree (decisions §2, §17.4): name trimmed, 1-64 characters, unique case-insensitively among siblings (the top
/// level counts as one sibling group); no self parent and no cycle; a category with children or product links
/// (archived products included) cannot be deleted. Lists are in path order.
/// </summary>
public sealed class V2CategoryService(IV2CatalogReader reader, V2CatalogWriter writer)
{
    private const int MaxName = 64;

    public async Task<V2Result<Page<CategoryResponse>>> ListAsync(
        string? search, string? parentId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var after, out var size) is { } invalid)
        {
            return invalid;
        }

        long? parent = null;
        if (!string.IsNullOrEmpty(parentId))
        {
            if (V2Text.Id(parentId, "parentId", out var parsed) is { } badParent)
            {
                return badParent;
            }

            parent = parsed;
        }

        var all = await reader.ListCategoriesAsync(cancellationToken);
        var byId = all.ToDictionary(c => c.Id);
        var normalized = string.IsNullOrWhiteSpace(search) ? null : V2Text.Normalize(search);
        var ordered = all
            .Where(c => parent is null || c.ParentId == parent)
            .Where(c => normalized is null || V2Text.Normalize(c.Name).Contains(normalized, StringComparison.Ordinal))
            .Select(c => (Category: c, Path: V2CatalogMapping.PathOf(c.Id, byId)))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Category.Id)
            .ToList();

        var start = 0;
        if (after is { } afterId)
        {
            var index = ordered.FindIndex(x => x.Category.Id == afterId);
            if (index < 0)
            {
                return V2Failure.Validation("cursor", "cursor is not valid for this list.");
            }

            start = index + 1;
        }

        var page = ordered.Skip(start).Take(size).ToList();
        var next = start + size < ordered.Count ? V2Paging.Encode(page[^1].Category.Id) : null;
        return new Page<CategoryResponse>(page.Select(x => V2CatalogMapping.ToResponse(x.Category, byId)).ToList(), next);
    }

    public async Task<V2Result<CategoryResponse>> CreateAsync(
        V2SessionPrincipal principal, CategoryWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request, out var name, out var parentId) is { } invalid)
        {
            return await writer.RejectAsync<CategoryResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.CategoryCreate, "新增分类", [], invalid, cancellationToken);
        }

        return await writer.RunAsync<CategoryResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.CategoryCreate, $"新增分类 {name}", [],
            async (scope, actor, now) =>
            {
                var all = (await scope.ListCategoriesAsync(cancellationToken)).ToDictionary(c => c.Id);
                if (CheckPlacement(all, null, name, parentId) is { } rejected)
                {
                    return rejected;
                }

                var id = await scope.InsertCategoryAsync(name, V2Text.Normalize(name), parentId, now, cancellationToken);
                var created = (await scope.ListCategoriesAsync(cancellationToken)).ToDictionary(c => c.Id);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.CategoryCreate, $"新增分类 {V2CatalogMapping.PathOf(id, created)}", true,
                    [new(SubjectTypes.Category, id, name)], actor.UserId, actor.DisplayName, actor.Role));
                return V2CatalogMapping.ToResponse(created[id], created);
            },
            cancellationToken);
    }

    public async Task<V2Result<CategoryResponse>> UpdateAsync(
        V2SessionPrincipal principal, string id, CategoryWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var categoryId);
        var invalid = Validate(request, out var name, out var parentId);
        var rejected = badId ?? invalid
            ?? (request.Version is null ? V2Failure.Validation("version", "version is required to update a category.") : null);
        if (rejected is not null)
        {
            IReadOnlyList<V2SubjectRecord> subjects = categoryId > 0 ? [new(SubjectTypes.Category, categoryId)] : [];
            return await writer.RejectAsync<CategoryResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.CategoryUpdate, "修改分类", subjects, rejected, cancellationToken);
        }

        var version = request.Version!.Value;
        var subject = V2CatalogMapping.Ref(SubjectTypes.Category, categoryId);
        return await writer.RunAsync<CategoryResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.CategoryUpdate, $"修改分类 {name}", [new(SubjectTypes.Category, categoryId)],
            async (scope, actor, now) =>
            {
                var all = (await scope.ListCategoriesAsync(cancellationToken)).ToDictionary(c => c.Id);
                if (!all.TryGetValue(categoryId, out var category))
                {
                    return V2Failure.NotFound("Category", subject);
                }

                if (category.Version != version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (parentId is { } parent && V2CatalogMapping.WithDescendants(categoryId, all.Values).Contains(parent))
                {
                    return V2Failure.Validation("parentId", "A category cannot be moved under itself or one of its descendants.");
                }

                if (CheckPlacement(all, categoryId, name, parentId) is { } rejected)
                {
                    return rejected;
                }

                var oldPath = V2CatalogMapping.PathOf(categoryId, all);
                if (!await scope.UpdateCategoryAsync(categoryId, version, name, V2Text.Normalize(name), parentId, now, cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                var updated = (await scope.ListCategoriesAsync(cancellationToken)).ToDictionary(c => c.Id);
                var newPath = V2CatalogMapping.PathOf(categoryId, updated);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.CategoryUpdate, $"修改分类 {oldPath} → {newPath}", true,
                    [new(SubjectTypes.Category, categoryId, name)], actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [new("path", oldPath, newPath)]));
                return V2CatalogMapping.ToResponse(updated[categoryId], updated);
            },
            cancellationToken);
    }

    public async Task<V2Result<V2None>> DeleteAsync(
        V2SessionPrincipal principal, string id, DeleteRequest? request, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var categoryId) is { } badId)
        {
            return await writer.RejectAsync<V2None>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.CategoryDelete, "删除分类", [], badId, cancellationToken);
        }

        var subject = V2CatalogMapping.Ref(SubjectTypes.Category, categoryId);
        return await writer.RunAsync<V2None>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.CategoryDelete, "删除分类", [new(SubjectTypes.Category, categoryId)],
            async (scope, actor, now) =>
            {
                var all = (await scope.ListCategoriesAsync(cancellationToken)).ToDictionary(c => c.Id);
                if (!all.TryGetValue(categoryId, out var category))
                {
                    return V2Failure.NotFound("Category", subject);
                }

                if (request?.Version is { } version && version != category.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (category.ChildCount > 0 || category.ProductCount > 0)
                {
                    return new V2Failure(
                        ProblemCodesV2.InUse,
                        $"The category has {category.ChildCount} child categories and {category.ProductCount} product link(s), archived products included.",
                        Subjects: [subject]);
                }

                var path = V2CatalogMapping.PathOf(categoryId, all);
                await scope.DeleteCategoryAsync(categoryId, cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.CategoryDelete, $"删除分类 {path}", true, [new(SubjectTypes.Category, categoryId, category.Name)],
                    actor.UserId, actor.DisplayName, actor.Role, Changes: [new("path", path, null)]));
                return V2None.Value;
            },
            cancellationToken);
    }

    private static V2Failure? Validate(CategoryWriteRequest request, out string name, out long? parentId)
    {
        parentId = null;
        if (V2Text.Name(request.Name, MaxName, "name", out name) is { } invalid)
        {
            return invalid;
        }

        if (request.ParentId is not null)
        {
            if (V2Text.Id(request.ParentId, "parentId", out var parent) is { } badParent)
            {
                return badParent;
            }

            parentId = parent;
        }

        return null;
    }

    /// <summary>The parent must exist and no sibling (same parent, top level included) may have the same normalized name.</summary>
    private static V2Failure? CheckPlacement(IReadOnlyDictionary<long, V2CategoryRecord> all, long? self, string name, long? parentId)
    {
        if (parentId is { } parent && !all.ContainsKey(parent))
        {
            return V2Failure.NotFound("Parent category", V2CatalogMapping.Ref(SubjectTypes.Category, parent)) with { Field = "parentId" };
        }

        var normalized = V2Text.Normalize(name);
        var sibling = all.Values.FirstOrDefault(c => c.Id != self && c.ParentId == parentId && V2Text.Normalize(c.Name) == normalized);
        return sibling is null
            ? null
            : new V2Failure(
                ProblemCodesV2.AlreadyExists,
                $"A sibling category named '{sibling.Name}' already exists.",
                "name",
                [V2CatalogMapping.Ref(SubjectTypes.Category, sibling.Id, sibling.Name)]);
    }
}
