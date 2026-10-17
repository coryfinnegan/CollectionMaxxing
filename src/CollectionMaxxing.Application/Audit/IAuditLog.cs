namespace CollectionMaxxing.Application.Audit;

/// <summary>The audit actions this app records. Names are stable: they are stored.</summary>
public static class AuditActions
{
    public const string ItemDeleted = "item.deleted";
    public const string ItemRestored = "item.restored";
    public const string ItemPurged = "item.purged";
    public const string ItemsBulkDeleted = "items.bulk_deleted";
    public const string ItemsBulkRestored = "items.bulk_restored";
}

/// <summary>
/// Writes the audit trail (docs/03, <c>audit_events</c>). <see cref="Record"/> adds the event to the current unit of
/// work: it is saved by the caller's <c>SaveChanges</c>, in the same transaction as the change it describes.
/// The actor comes from the signed-in caller; the address is stored only as a salted hash.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// <paramref name="details"/> may hold ids, counts and flags only (Guid, int, long, bool), never titles, notes or other
    /// free text: audit rows outlive the data they describe. Anything else throws.
    /// </summary>
    void Record(string action, string? entityType, Guid? entityId, Guid? libraryId, IReadOnlyDictionary<string, object>? details = null);
}

/// <summary>The network address of the request being served, or null outside a request (a background job).</summary>
public interface IRequestOrigin
{
    string? Address { get; }
}
