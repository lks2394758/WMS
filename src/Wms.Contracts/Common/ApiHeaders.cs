namespace Wms.Contracts.Common;

public static class ApiHeaders
{
    /// <summary>Required on every inventory write: a client-generated UUID, reused on retry (guide §8.1).</summary>
    public const string IdempotencyKey = "Idempotency-Key";
}
