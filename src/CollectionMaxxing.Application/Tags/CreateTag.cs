using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tags;

/// <summary>
/// Use case: add a tag to a library. A name that differs only in case from an existing one is rejected by the
/// database's unique index (citext), which the API turns into a 409.
/// </summary>
public static class CreateTag
{
    public sealed record Command(Guid LibraryId, string? Name);

    public sealed class Handler(IAppDbContext db, IIdGenerator ids)
    {
        public async Task<Result<TagDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (!await db.Libraries.AnyAsync(l => l.Id == command.LibraryId, cancellationToken))
            {
                return Errors.LibraryNotFound;
            }

            var name = Tag.ValidateName(command.Name);
            if (name.Error is { } error)
            {
                return error;
            }

            var tag = new Tag { Id = ids.NewId(), LibraryId = command.LibraryId, Name = name.Value };
            db.Tags.Add(tag);
            await db.SaveChangesAsync(cancellationToken);
            return new TagDto(tag.Id, tag.Name, 0);
        }
    }
}
