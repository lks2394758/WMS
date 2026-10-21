using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Wms.Api.Errors;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Common;
using Wms.Domain.Common;

namespace Wms.Api.V2;

/// <summary>v2 <c>application/problem+json</c>: status from <see cref="ProblemCodesV2.HttpStatus"/>, stable <c>code</c>.</summary>
internal static class V2Problems
{
    private const string TypeBase = "https://wms-api.company.intra/problems/";

    public static int StatusOf(string code) =>
        ProblemCodesV2.HttpStatus.TryGetValue(code, out var status) ? status : StatusCodes.Status500InternalServerError;

    /// <summary>Fallback code for problems raised by the framework itself in v2 mode.</summary>
    public static string CodeOf(int? status) => status switch
    {
        StatusCodes.Status401Unauthorized => ProblemCodesV2.Unauthenticated,
        StatusCodes.Status403Forbidden => ProblemCodesV2.Forbidden,
        StatusCodes.Status404NotFound => ProblemCodesV2.NotFound,
        StatusCodes.Status413PayloadTooLarge => ProblemCodesV2.PayloadTooLarge,
        StatusCodes.Status429TooManyRequests => ProblemCodesV2.RateLimited,
        >= StatusCodes.Status500InternalServerError => ProblemCodesV2.InternalError,
        _ => ProblemCodesV2.ValidationFailed,
    };

    public static ProblemDetails Create(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var problem = new ProblemDetails
        {
            Status = StatusOf(error.Code),
            Title = TitleOf(error.Code),
            Type = TypeBase + error.Code.ToLowerInvariant().Replace('_', '-'),
            Detail = error.Message,
        };
        problem.Extensions["code"] = error.Code;
        return problem;
    }

    /// <summary>A controller result; a failure naming a field becomes 400 with <c>errors</c>.</summary>
    public static ObjectResult Result(HttpContext httpContext, DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code == ProblemCodesV2.ValidationFailed && error.Field is not null)
        {
            return ValidationProblems.Create(httpContext, error.Message, new Dictionary<string, string[]> { [error.Field] = [error.Message] });
        }

        // The factory runs CustomizeProblemDetails (traceId); the stable code is set afterwards.
        var template = Create(error);
        var factory = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateProblemDetails(httpContext, template.Status, template.Title, template.Type, template.Detail);
        problem.Extensions["code"] = error.Code;
        return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { ValidationProblems.ProblemJson } };
    }

    /// <summary>
    /// A v2 use-case failure: status from the code; <c>errors</c> for the named field (400 validation, or alongside other
    /// codes such as <c>ALREADY_EXISTS</c> on <c>sku</c>); <c>subjects</c> naming the objects concerned.
    /// </summary>
    public static ObjectResult Result(HttpContext httpContext, V2Failure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var result = Result(httpContext, new DomainError(failure.Code, failure.Message, failure.Field));
        if (result.Value is ProblemDetails problem)
        {
            if (failure.Field is not null && failure.Code != ProblemCodesV2.ValidationFailed)
            {
                problem.Extensions["errors"] = new Dictionary<string, string[]> { [failure.Field] = [failure.Message] };
            }

            if (failure.Subjects is { Count: > 0 } subjects)
            {
                problem.Extensions["subjects"] = subjects;
            }
        }

        return result;
    }

    /// <summary>Writes the problem outside MVC (authentication challenge, rate limiter); the trace ID is added by the service.</summary>
    public static async Task WriteAsync(HttpContext httpContext, DomainError error)
    {
        var problem = Create(error);
        httpContext.Response.StatusCode = problem.Status!.Value;
        var writer = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await writer.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }

    private static string TitleOf(string code)
    {
        var words = code.ToLowerInvariant().Replace('_', ' ');
        return string.Concat(words[..1].ToUpper(CultureInfo.InvariantCulture), words[1..]);
    }
}
