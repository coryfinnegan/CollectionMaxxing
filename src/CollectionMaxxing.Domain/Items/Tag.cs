using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Items;

/// <summary>A label you put on items. Names are unique per library, ignoring case.</summary>
public sealed class Tag : ILibraryScoped, IConcurrencyVersioned
{
    public const int MaxNameLength = 50;

    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public string Name { get; set; } = string.Empty;

    public uint Version { get; private set; }

    /// <summary>Trims the name and checks it: required, at most <see cref="MaxNameLength"/>, no control characters.</summary>
    public static Result<string> ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new DomainError(ErrorKind.Validation, "tag.name_required", "A name is required.", "name");
        }

        if (trimmed.Length > MaxNameLength)
        {
            return new DomainError(ErrorKind.Validation, "tag.name_too_long", $"A name can be at most {MaxNameLength} characters.", "name");
        }

        if (trimmed.Any(char.IsControl))
        {
            return new DomainError(ErrorKind.Validation, "tag.name_invalid", "A name can't contain control characters.", "name");
        }

        return trimmed;
    }
}

/// <summary>Puts a tag on an item. Both must be in <see cref="LibraryId"/>, which the database enforces.</summary>
public sealed class ItemTag : ILibraryScoped
{
    public Guid LibraryId { get; init; }

    public Guid ItemId { get; init; }

    public Guid TagId { get; init; }
}
