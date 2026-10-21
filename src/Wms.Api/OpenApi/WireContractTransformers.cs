using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Wms.Contracts.Common;

namespace Wms.Api.OpenApi;

/// <summary>
/// Narrows the generated document to the wire contract in docs/api-conventions.md where the framework defaults
/// describe more (or less) than the API promises. The result is committed as <c>contracts/openapi/wms.v1.json</c>.
/// <list type="bullet">
/// <item>Stable <c>info</c> and an <c>operationId</c> of controller + action (e.g. <c>InventoryReceive</c>) for client generators.</item>
/// <item>Bodies are <c>application/json</c> only; the extra <c>text/json</c> and <c>text/plain</c> formatter types are not part of v1.</item>
/// <item>Numbers are JSON numbers. The Web serializer defaults also read numeric strings; clients must not rely on that.</item>
/// <item>Null properties are omitted on the wire (<see cref="WmsJson"/>), so a nullable property is never <c>required</c>.</item>
/// <item><c>Idempotency-Key</c> is a required UUID header, and the success response declares <c>Idempotent-Replayed</c>.</item>
/// <item>The problem <c>code</c> lists the stable <see cref="ProblemCodes"/>.</item>
/// </list>
/// </summary>
internal sealed class WireContractTransformers
    : IOpenApiDocumentTransformer, IOpenApiOperationTransformer, IOpenApiSchemaTransformer
{
    private const string Json = "application/json";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Info = new OpenApiInfo
        {
            Title = "WMS API",
            Version = "v1",
            Description = "Single-warehouse inventory API. Conventions, permissions and error codes: docs/api-conventions.md.",
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Description.ActionDescriptor is ControllerActionDescriptor action)
        {
            operation.OperationId = action.ControllerName + action.ActionName;
        }

        KeepOnlyJson(operation.RequestBody?.Content);
        foreach (var response in operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>())
        {
            KeepOnlyJson(response.Content);
        }

        var idempotencyKey = operation.Parameters?.OfType<OpenApiParameter>()
            .FirstOrDefault(p => p.In == ParameterLocation.Header && p.Name == ApiHeaders.IdempotencyKey);
        if (idempotencyKey is not null)
        {
            idempotencyKey.Required = true;
            idempotencyKey.Description = "Client-generated UUID, one per user action and reused on every retry of it.";
            idempotencyKey.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
            if (operation.Responses?.GetValueOrDefault("200") is OpenApiResponse ok)
            {
                ok.Headers ??= new Dictionary<string, IOpenApiHeader>();
                ok.Headers[ApiHeaders.IdempotentReplayed] = new OpenApiHeader
                {
                    Description = "Present when the response is the stored original of an earlier request with the same key.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("true")] },
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        const JsonSchemaType numeric = JsonSchemaType.Integer | JsonSchemaType.Number;
        if (schema.Type is { } type && (type & numeric) != 0 && (type & JsonSchemaType.String) != 0)
        {
            schema.Type = type & ~JsonSchemaType.String;
            schema.Pattern = null;
        }

        if (schema.Required is { Count: > 0 } required && schema.Properties is not null)
        {
            foreach (var (name, property) in schema.Properties)
            {
                if (property.Type is { } propertyType && (propertyType & JsonSchemaType.Null) != 0)
                {
                    required.Remove(name);
                }
            }
        }

        if (context.JsonTypeInfo.Type == typeof(WmsProblemDetails)
            && schema.Properties?.TryGetValue("code", out var codeProperty) == true
            && codeProperty is OpenApiSchema code)
        {
            code.Enum = [.. ProblemCodes.All.Select(c => (JsonNode)JsonValue.Create(c))];
        }

        return Task.CompletedTask;
    }

    private static void KeepOnlyJson(IDictionary<string, OpenApiMediaType>? content)
    {
        if (content is null || !content.ContainsKey(Json))
        {
            return;
        }

        foreach (var mediaType in content.Keys.Where(k => k != Json).ToList())
        {
            content.Remove(mediaType);
        }
    }
}
