using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>Use case: the library's "Needs attention" list — its provisional records that still need acting on, stalest first.</summary>
public static class ListProvisionals
{
    public sealed record Query(Guid LibraryId);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result<ProvisionalsDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            if (!await db.Libraries.AnyAsync(l => l.Id == query.LibraryId, cancellationToken))
            {
                return Errors.LibraryNotFound;
            }

            var open = await db.ProvisionalRecords.AsNoTracking()
                .Where(p => p.LibraryId == query.LibraryId && p.ResolvedAt == null)
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .ToListAsync(cancellationToken);
            return new ProvisionalsDto(open.Select(ProvisionalProjection.ToDto).ToList());
        }
    }
}
