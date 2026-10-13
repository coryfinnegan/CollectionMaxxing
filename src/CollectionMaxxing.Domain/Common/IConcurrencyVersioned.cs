namespace CollectionMaxxing.Domain.Common;

/// <summary>
/// An entity whose rows can be edited by two people at once. <see cref="Version"/> is Postgres' <c>xmin</c> system
/// column: it changes on every update, so a save that loaded a stale row fails instead of overwriting the newer one.
/// Infrastructure maps it; the API turns it into an <c>ETag</c>.
/// </summary>
public interface IConcurrencyVersioned
{
    uint Version { get; }
}
