using Microsoft.AspNetCore.Mvc;
using Wms.Api.Errors;
using Wms.Contracts.Common;
using Wms.Api.Validation;
using Wms.Domain.Common;

namespace Wms.Api.Controllers;

/// <summary>
/// Turns use-case results into JSON responses or <c>application/problem+json</c> with the stable code. Every endpoint
/// can answer 401 (no or invalid token, or failed sign-in) and 429 (rate limit).
/// </summary>
[ApiController]
[ProducesResponseType<WmsProblemDetails>(StatusCodes.Status401Unauthorized, ValidationProblems.ProblemJson)]
[ProducesResponseType<WmsProblemDetails>(StatusCodes.Status429TooManyRequests, ValidationProblems.ProblemJson)]
public abstract class WmsControllerBase : ControllerBase
{
    protected const string ProblemJson = ValidationProblems.ProblemJson;

    protected ActionResult Failure(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Field is not null)
        {
            return ValidationProblems.Create(HttpContext, error.Message, new Dictionary<string, string[]>
            {
                [error.Field] = [error.Message],
            });
        }

        var status = ProblemCatalog.StatusOf(error.Code);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, status, ProblemCatalog.TitleOf(error.Code), ProblemCatalog.TypeOf(error.Code), error.Message);
        problem.Extensions["code"] = error.Code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { ProblemJson } };
    }

    /// <summary>400 <c>VALIDATION_FAILED</c> with field-level <c>errors</c> (see <see cref="ValidationProblems"/>).</summary>
    private protected ActionResult ValidationFailure(FieldErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return ValidationProblems.Create(HttpContext, errors.ToDictionary());
    }

    protected ActionResult<T> FromResult<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : Failure(result.Error!);
}
