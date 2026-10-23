using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using Microsoft.AspNetCore.Authentication;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Runs right after authentication. For a signed-in caller it loads their library memberships once and stores them in
/// the request's <see cref="CurrentUser"/>, which the database library filter and the membership filter both read.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser currentUser, IMembershipReader memberships, IUserDirectory users)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // A valid Auth0 token whose identity nobody linked: refused, and nothing is created for it (docs/14, "Linking").
            if (AuthSetup.IsBearerCall(context.Request) && CurrentSession.UserId(context.User) is null)
            {
                await AuthProblems.NotLinked().ExecuteAsync(context);
                return;
            }

            if (CurrentSession.UserId(context.User) is not { } userId)
            {
                // Signed in as someone we can't identify: refuse rather than carry on as nobody.
                await ProblemResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "The sign-in has no valid user id.")
                    .ExecuteAsync(context);
                return;
            }

            // A member who was removed (or an account that was deleted) is refused from their very next call, whatever their cookie or
            // access token still says. The cookie is forgotten too, so they are not stuck with a dead session.
            if (await users.IsDeactivatedAsync(userId, context.RequestAborted))
            {
                await context.SignOutAsync(AuthCookie.Scheme);
                await ProblemResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "This account is no longer active.", "account.deactivated")
                    .ExecuteAsync(context);
                return;
            }

            currentUser.Initialize(userId, await memberships.GetMembershipsAsync(userId, context.RequestAborted));
        }

        await next(context);
    }
}
