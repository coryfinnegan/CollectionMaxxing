namespace CollectionMaxxing.Application.Common;

/// <summary>Source of new entity ids (UUIDv7), injectable so tests are deterministic.</summary>
public interface IIdGenerator
{
    Guid NewId();
}
