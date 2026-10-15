using System.Text.Json;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Accounts.Deletion;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Accounts.Deletion;

/// <summary>
/// Body of <c>DELETE /api/v1/me</c>: the passkey assertion the browser made for this request. The browser first asks
/// <c>POST /account/passkey/request-options</c> for a challenge, signs it with the passkey, and sends the result here.
/// There is no user id: the account deleted is always the signed-in caller's.
/// </summary>
public sealed record DeleteAccountRequest(JsonElement? Credential = null);

public static class DeleteAccountEndpoint
{
    public static IServiceCollection AddAccountDeletion(this IServiceCollection services)
    {
        services.AddScoped<FreshAssertion>();
        services.AddScoped<DeleteAccount.Handler>();
        return services.AddAccountPurge();
    }

    /// <summary><c>DELETE /api/v1/me</c> (docs/04, "Account and session"). Authorization: signed in, plus a passkey assertion made for this very request.</summary>
    public static RouteHandlerBuilder MapDeleteAccount(this IEndpointRouteBuilder app) =>
        app.MapDelete(
                "/api/v1/me",
                async ([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] DeleteAccountRequest? body, HttpContext http, ICurrentUser user, FreshAssertion assertion, DeleteAccount.Handler handler, CancellationToken cancellationToken) =>
                {
                    var userId = user.UserId!.Value;
                    if (!await assertion.VerifyAsync(http, userId, body?.Credential))
                    {
                        return AuthProblems.ReauthRequired();
                    }

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
            .RequireAuthorization();
}

/// <summary>
/// Proof that the person is here now: a passkey assertion made for this request, which is stricter than the five-minute
/// <c>RequireRecentPasskey</c> rule. The browser asks for a challenge (<c>POST /account/passkey/request-options</c>, which the server remembers
/// in the sealed ceremony cookie), signs it, and sends the assertion with the delete. Each challenge can be answered once, within five
/// minutes, and only by a passkey of the signed-in user. A sign-in a moment ago, an assertion kept from an earlier request or one used
/// before all fail, and so does somebody else's passkey.
/// </summary>
public sealed class FreshAssertion(IPasskeyHandler<AppUser> passkeys, UserManager<AppUser> users, CeremonyCookie ceremonies, TimeProvider time)
{
    public async Task<bool> VerifyAsync(HttpContext http, Guid expectedUserId, JsonElement? credential)
    {
        var ceremony = ceremonies.Read(http, CeremonyKind.SignIn);
        ceremonies.Clear(http); // one challenge, one try, whatever happens next
        if (ceremony is null || credential is not { ValueKind: JsonValueKind.Object } answer)
        {
            return false;
        }

        if (time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(ceremony.IssuedAt) > RecentPasskeyFilter.MaxAge)
        {
            return false;
        }

        PasskeyAssertionResult<AppUser> asserted;
        try
        {
            asserted = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
            {
                HttpContext = http,
                CredentialJson = answer.GetRawText(),
                AssertionState = ceremony.State,
            });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return false;
        }

        if (!asserted.Succeeded || asserted.User.Id != expectedUserId || asserted.User.DeletedAt is not null)
        {
            return false;
        }

        await users.AddOrUpdatePasskeyAsync(asserted.User, asserted.Passkey); // keeps the signature counter current, so this assertion can't be replayed
        return true;
    }
}
