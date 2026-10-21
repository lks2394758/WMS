using Wms.Contracts.Common;

namespace Wms.Api.Errors;

/// <summary>Fallback code for problems raised by the framework itself (unknown route, unhandled exception, ...).</summary>
internal static class ProblemCodeMapper
{
    public static string FromStatus(int? status) => status switch
    {
        StatusCodes.Status401Unauthorized => ProblemCodes.Unauthenticated,
        StatusCodes.Status403Forbidden => ProblemCodes.Forbidden,
        StatusCodes.Status404NotFound => ProblemCodes.NotFound,
        StatusCodes.Status429TooManyRequests => ProblemCodes.RateLimited,
        >= StatusCodes.Status500InternalServerError => ProblemCodes.InternalError,
        _ => ProblemCodes.ValidationFailed,
    };
}
