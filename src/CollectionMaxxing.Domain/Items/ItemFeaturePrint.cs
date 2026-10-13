using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Items;

/// <summary>
/// Backup of the phone's Apple feature print of an item's artwork. Opaque to the server: it never reads <see cref="Data"/>,
/// it only stores it so a new phone can restore instant matching.
/// </summary>
public sealed class ItemFeaturePrint : ILibraryScoped, IConcurrencyVersioned
{
    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public Guid ItemId { get; init; }

    public Guid ReleaseId { get; init; }

    /// <summary>Apple's feature-print revision; prints of different revisions can't be compared.</summary>
#pragma warning disable CA1819 // Opaque blob: the server stores and returns it, never inspects it.
    public byte[] Data { get; set; } = [];
#pragma warning restore CA1819

    public int Revision { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public uint Version { get; private set; }
}
