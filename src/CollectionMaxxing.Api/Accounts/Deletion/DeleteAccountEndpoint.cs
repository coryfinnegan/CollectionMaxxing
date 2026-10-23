using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Accounts.Deletion;
using Microsoft.AspNetCore.Authentication;

namespace CollectionMaxxing.Api.Accounts.Deletion;

public static class DeleteAccountEndpoint
{
    public static IServiceCollection AddAccountDeletion(this IServiceCollection services)
    {
        services.AddScoped<DeleteAccount.Handler>();
        return services.AddAccountPurge();
    }

    /// <summary>
    /// <c>DELETE /api/v1/me</c> (docs/04, "Account and session"). No body: the account deleted is always the signed-in caller's.
    /// Authorization: signed in, with a sign-in from the last five minutes (401 <c>reauth_required</c> otherwise).
    /// </summary>
    public static RouteHandlerBuilder MapDeleteAccount(this IEndpointRouteBuilder app) =>
        app.MapDelete(
                "/api/v1/me",
                async (HttpContext http, ICurrentUser user, DeleteAccount.Handler handler, CancellationToken cancellationToken) =>
                {
                    var userId = user.UserId!.Value;
                    var result = await handler.HandleAsync(userId, cancellationToken);
                    if (!result.IsSuccess)
                    {
                        return result.Error!.ToProblem();
                    }

                    await http.SignOutAsync(AuthCookie.Scheme);
                    return Results.NoContent();
                })
            .WithName("DeleteMe").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireAuthorization().RequireRecentSignIn();
}
