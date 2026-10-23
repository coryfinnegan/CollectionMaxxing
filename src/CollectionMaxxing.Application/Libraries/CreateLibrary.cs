using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Libraries;

/// <summary>Settings under <c>Libraries:</c> (docs/15, D-3). A config value, not a setting in the app.</summary>
public sealed class LibraryOptions
{
    public const string SectionName = "Libraries";

    /// <summary>How many libraries one account may own. Libraries joined by invite don't count.</summary>
    public int MaxOwnedPerUser { get; set; } = 3;
}

/// <summary>Writes a new library and its owner. Implemented by Infrastructure, where the owner's row can be locked.</summary>
public interface ILibraryStore
{
    /// <summary>
    /// In one transaction: locks the owner's account, counts the libraries they own, and unless that is already
    /// <paramref name="maxOwned"/> adds <paramref name="library"/> with them as its one owner. Two creates at once can't pass the limit.
    /// </summary>
    Task<Result> CreateOwnedAsync(Library library, Guid ownerId, int maxOwned, CancellationToken cancellationToken);
}

/// <summary>Why a library request was refused.</summary>
public static class LibraryErrors
{
    public static readonly DomainError NameRequired =
        new(ErrorKind.Validation, "library.name_required", "Give the library a name.", "name");

    public static readonly DomainError NameTooLong =
        new(ErrorKind.Validation, "library.name_too_long", $"A library name can be at most {CreateLibrary.MaxNameLength} characters.", "name");

    public static DomainError LimitReached(int max) =>
        new(ErrorKind.Conflict, "library.limit_reached", $"You already own {max} libraries, which is the most you can have.");
}

/// <summary>Use case: anyone signed in creates a library and becomes its only owner (docs/15, "Create your library").</summary>
public static class CreateLibrary
{
    /// <summary>A new library's name is 1 to 80 characters once trimmed (the contract in docs/15).</summary>
    public const int MaxNameLength = 80;

    public sealed class Handler(ILibraryStore store, ICurrentUser currentUser, IIdGenerator ids, TimeProvider time, IOptions<LibraryOptions> options)
    {
        public async Task<Result<LibraryDto>> HandleAsync(string? name, CancellationToken cancellationToken)
        {
            var trimmed = name?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                return LibraryErrors.NameRequired;
            }

            if (trimmed.Length > MaxNameLength)
            {
                return LibraryErrors.NameTooLong;
            }

            var userId = currentUser.UserId ?? throw new InvalidOperationException("Creating a library needs a signed-in caller; the endpoint requires authorization.");
            var library = new Library(ids.NewId(), trimmed, time.GetUtcNow());
            var max = Math.Max(0, options.Value.MaxOwnedPerUser);
            var created = await store.CreateOwnedAsync(library, userId, max, cancellationToken);
            return created.IsSuccess ? new LibraryDto(library.Id, library.Name, library.CreatedAt, LibraryRole.Owner) : created.Error!;
        }
    }
}
