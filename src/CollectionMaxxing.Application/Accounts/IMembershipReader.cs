using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>
/// Looks up which libraries a user belongs to. It is the one place that reads memberships
/// without the library filter, because the filter itself is built from this answer.
/// </summary>
public interface IMembershipReader
{
    Task<IReadOnlyList<Membership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken);
}
