namespace CollectionMaxxing.Domain.Libraries;

/// <summary>The unit of sharing: everything a household owns lives in one library.</summary>
public sealed class Library
{
    public const int MaxNameLength = 100;

    // EF Core materialises entities through this constructor.
    private Library()
    {
        Name = string.Empty;
    }

    public Library(Guid id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
