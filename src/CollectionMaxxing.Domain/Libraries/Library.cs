namespace CollectionMaxxing.Domain.Libraries;

/// <summary>The unit of sharing: everything a household owns lives in one library.</summary>
public sealed class Library : Common.IConcurrencyVersioned
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

    public uint Version { get; private set; }

    /// <summary>Renames the library. The name is trimmed; it can't be blank or longer than <see cref="MaxNameLength"/>.</summary>
    public Common.Result Rename(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Common.Result.Failure(new Common.DomainError(Common.ErrorKind.Validation, "library.name_required", "Give the library a name.", "name"));
        }

        if (trimmed.Length > MaxNameLength)
        {
            return Common.Result.Failure(new Common.DomainError(
                Common.ErrorKind.Validation, "library.name_too_long", $"A library name can be at most {MaxNameLength} characters.", "name"));
        }

        Name = trimmed;
        return Common.Result.Success();
    }
}
