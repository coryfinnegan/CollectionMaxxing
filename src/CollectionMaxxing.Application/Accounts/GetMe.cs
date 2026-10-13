using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Accounts;

public sealed record MembershipDto(Guid LibraryId, LibraryRole Role);

/// <summary>Use case: who am I, and which libraries can I use? No database access: the request already knows.</summary>
public static class GetMe
{
    public sealed record Response(Guid Id, IReadOnlyList<MembershipDto> Memberships);

    public sealed class Handler(ICurrentUser currentUser)
    {
        public Response Handle()
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("GetMe needs a signed-in caller; the endpoint requires authorization.");
            return new Response(userId, currentUser.Memberships.Select(m => new MembershipDto(m.LibraryId, m.Role)).ToList());
        }
    }
}
