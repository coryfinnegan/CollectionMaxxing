using CollectionMaxxing.Api.Auth.Pairing;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Runs right after authentication. For a signed-in caller it loads their library memberships once and stores them in
/// the request's <see cref="CurrentUser"/>, which the database library filter and the membership filter both read.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser currentUser, IMembershipReader memberships)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (CurrentSession.UserId(context.User) is not { } userId)
            {
                // Signed in as someone we can't identify: refuse rather than carry on as nobody.
                await ProblemResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "The sign-in has no valid user id.")
                    .ExecuteAsync(context);
                return;
            }

            currentUser.Initialize(userId, await memberships.GetMembershipsAsync(userId, context.RequestAborted));
        }

        await next(context);
    }
}
