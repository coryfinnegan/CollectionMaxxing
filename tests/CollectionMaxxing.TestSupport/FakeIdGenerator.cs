using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.TestSupport;

/// <summary>Hands out 00000001-0000-0000-0000-000000000000, 00000002-..., so tests can predict ids.</summary>
public sealed class FakeIdGenerator : IIdGenerator
{
    private int _next;

    public Guid NewId() => new(++_next, 0, 0, new byte[8]);
}
