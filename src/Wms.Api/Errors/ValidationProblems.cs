using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wms.Contracts.Common;

namespace Wms.Api.Errors;

/// <summary>
/// Request binding and validation failures as RFC 9457 problem details: <c>VALIDATION_FAILED</c>, the catalog title
/// and type, and <c>errors</c> keyed by the wire field name (<c>lines[0].quantity</c>, <c>limit</c>, ...).
/// </summary>
internal static class ValidationProblems
{
    public const string ProblemJson = "application/problem+json";

    /// <summary>The <c>errors</c> key for a missing or unreadable request body.</summary>
    public const string BodyField = "body";

    private const string InvalidFieldsDetail = "One or more request fields are invalid.";

    /// <summary>Replaces the framework's automatic 400 response for <c>[ApiController]</c> model-state errors.</summary>
    public static IActionResult FromModelState(ActionContext context)
    {
        // A missing body is also reported under the C# parameter name ("request"); clients only know "body".
        var bodyParameters = context.ActionDescriptor.Parameters
            .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var errors = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .GroupBy(entry => bodyParameters.Contains(entry.Key) ? BodyField : FieldName(entry.Key), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(entry => entry.Value!.Errors).Select(MessageOf).Distinct().ToArray(),
                StringComparer.Ordinal);

        return Create(context.HttpContext, InvalidFieldsDetail, errors);
    }

    public static ObjectResult Create(HttpContext httpContext, IEnumerable<KeyValuePair<string, string[]>> errors) =>
        Create(httpContext, InvalidFieldsDetail, errors);

    public static ObjectResult Create(HttpContext httpContext, string detail, IEnumerable<KeyValuePair<string, string[]>> errors)
    {
        var factory = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(
            httpContext,
            new ModelStateDictionary(),
            StatusCodes.Status400BadRequest,
            ProblemCatalog.TitleOf(ProblemCodes.ValidationFailed),
            ProblemCatalog.TypeOf(ProblemCodes.ValidationFailed),
            detail);
        foreach (var (field, messages) in errors)
        {
            problem.Errors[field] = messages;
        }

        problem.Extensions["code"] = ProblemCodes.ValidationFailed;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { ProblemJson } };
    }

    /// <summary>System.Text.Json reports body errors as JSON paths (<c>$.lines[0].quantity</c>); clients see field names.</summary>
    private static string FieldName(string key) => key switch
    {
        "" or "$" => BodyField,
        _ when key.StartsWith("$.", StringComparison.Ordinal) => key[2..],
        _ => key,
    };

    private static string MessageOf(ModelError error) =>
        string.IsNullOrEmpty(error.ErrorMessage) ? "The value is not valid." : error.ErrorMessage;
}
