using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Persistence;

public sealed class MembershipReader(IAppDbContext db) : IMembershipReader
{
    public async Task<IReadOnlyList<Membership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.LibraryMembers
            .IgnoreQueryFilters() // the filter is built from this very answer
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new Membership(m.LibraryId, m.Role))
            .ToListAsync(cancellationToken);
}
