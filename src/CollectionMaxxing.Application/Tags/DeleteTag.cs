using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tags;

/// <summary>Use case: delete a tag. The database cascades the removal to the item-tag links.</summary>
public static class DeleteTag
{
    public sealed record Command(Guid LibraryId, Guid Id);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            // The library filter makes a tag in another library look like one that doesn't exist.
            var tag = await db.Tags.SingleOrDefaultAsync(t => t.LibraryId == command.LibraryId && t.Id == command.Id, cancellationToken);
            if (tag is null)
            {
                return Result.Failure(new DomainError(ErrorKind.NotFound, "tag.not_found", "That tag doesn't exist."));
            }

            db.Tags.Remove(tag);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
