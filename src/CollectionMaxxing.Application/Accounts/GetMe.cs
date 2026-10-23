using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Accounts;

public sealed record MembershipDto(Guid LibraryId, LibraryRole Role);

/// <summary>
/// Who the caller is: their account id, their memberships, and (ONB-1 addendum) the account's display name and email, as the account holds
/// them (from Google or Apple when it was made). <c>Email</c> is null when the sign-in gave none.
/// </summary>
public sealed record MeResponse(Guid Id, IReadOnlyList<MembershipDto> Memberships, string DisplayName, string? Email);

/// <summary>Use case: who am I, and which libraries can I use? The memberships come from the request; the name and email from the account.</summary>
public static class GetMe
{
    public sealed class Handler(ICurrentUser currentUser, IUserDirectory users)
    {
        public async Task<MeResponse> HandleAsync(CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("GetMe needs a signed-in caller; the endpoint requires authorization.");
            var profile = await users.GetProfileAsync(userId, cancellationToken);
            return new MeResponse(
                userId,
                currentUser.Memberships.Select(m => new MembershipDto(m.LibraryId, m.Role)).ToList(),
                profile?.DisplayName ?? string.Empty,
                profile?.Email);
        }
    }
}
