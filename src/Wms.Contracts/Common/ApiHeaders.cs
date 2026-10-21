namespace Wms.Contracts.Common;

public static class ApiHeaders
{
    /// <summary>Required on every inventory write: a client-generated UUID, reused on retry (guide §8.1).</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>Set to <c>true</c> on a response that is the stored original of an earlier request with the same key.</summary>
    public const string IdempotentReplayed = "Idempotent-Replayed";

    /// <summary>
    /// Optional on every request; echoed on every response. A client value of 1-64 characters from
    /// <c>[A-Za-z0-9._:-]</c> is kept, anything else is replaced by a server-generated ID. Logged with the request.
    /// </summary>
    public const string CorrelationId = "X-Correlation-ID";
}
