namespace Wms.Persistence.Abstractions;

/// <summary>
/// A write could not obtain its lock in time, or a row changed underneath it (optimistic concurrency token). Nothing
/// was committed: the caller may retry the same action, with the same Idempotency-Key. Providers throw this instead of
/// their driver-specific exception so the application layer stays provider-neutral.
/// </summary>
public sealed class PersistenceConflictException : Exception
{
    public PersistenceConflictException()
        : this("The data store is busy with another change; nothing was committed.")
    {
    }

    public PersistenceConflictException(string message)
        : base(message)
    {
    }

    public PersistenceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
