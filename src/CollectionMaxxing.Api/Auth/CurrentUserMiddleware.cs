using System.Security.Claims;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using Microsoft.AspNetCore.Authentication;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Runs right after authentication. For a signed-in caller it loads their library memberships once and stores them in the request's
/// <see cref="CurrentUser"/>, which the database library filter and the membership filter both read. The first bearer call of an Auth0
/// identity nobody has yet creates its account here (docs/15): the name and email come from Auth0's <c>/userinfo</c>, never the request.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser currentUser, IMembershipReader memberships, IUserDirectory users)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (AuthSetup.IsBearerCall(context.Request) && CurrentSession.UserId(context.User) is null)
            {
                if (await CreateBearerAccountAsync(context) is { } refused)
                {
                    await refused.ExecuteAsync(context);
                    return;
                }
            }

            if (CurrentSession.UserId(context.User) is not { } userId)
            {
                // Signed in as someone we can't identify: refuse rather than carry on as nobody.
                await ProblemResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "The sign-in has no valid user id.")
                    .ExecuteAsync(context);
                return;
            }

            // A deleted account is refused from its very next call, whatever its cookie or access token still says. The cookie is
            // forgotten too, so they are not stuck with a dead session. (Removing a member ends only that membership: see below.)
            if (await users.IsDeactivatedAsync(userId, context.RequestAborted))
            {
                await context.SignOutAsync(AuthCookie.Scheme);
                await ProblemResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "This account is no longer active.", "account.deactivated")
                    .ExecuteAsync(context);
                return;
            }

            // Memberships are read on every request, so a member who left or was removed loses that library on their very next call.
            currentUser.Initialize(userId, await memberships.GetMembershipsAsync(userId, context.RequestAborted));
        }

        await next(context);
    }

    /// <summary>
    /// A valid Auth0 access token whose <c>sub</c> nobody has: make its account, limited per client, and carry on as that account. Returns
    /// the answer to give instead when that can't be done now (429, or 503 while Auth0 can't be asked); nothing is created then.
    /// </summary>
    private static async Task<IResult?> CreateBearerAccountAsync(HttpContext context)
    {
        var services = context.RequestServices;
        if (context.User.FindFirstValue("sub") is not { Length: > 0 } subject)
        {
            return null; // the 401 below
        }

        var client = services.GetRequiredService<ClientAddressResolver>().Resolve(context);
        if (services.GetRequiredService<AccountLimits>().TryTakeNewAccount(client) is { } wait)
        {
            return AccountLimits.Refused(context, wait);
        }

        var token = context.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var profile = await services.GetRequiredService<Auth0UserInfo>().GetAsync(subject, token, context.RequestAborted);
        if (profile is null)
        {
            return AuthProblems.ProfileUnavailable();
        }

        var created = await services.GetRequiredService<CreateAccount.Handler>().HandleAsync(profile, context.RequestAborted);
        if (!created.IsSuccess)
        {
            return null; // deleted meanwhile: the 401 below
        }

        var identity = new ClaimsIdentity(context.User.Claims, Auth0Setup.BearerScheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, created.Value.UserId.ToString()));
        identity.AddClaim(new Claim(CurrentSession.LoginIdClaim, created.Value.LoginId.ToString()));
        context.User = new ClaimsPrincipal(identity);
        return null;
    }
}
