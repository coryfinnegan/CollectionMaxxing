using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.Infrastructure.Ids;

/// <summary>UUIDv7 ids: time-ordered, so they index well and sort by creation.</summary>
public sealed class Uuid7IdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}
