using System.Text.Json;
using CollectionMaxxing.Api.Invites;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// The WebAuthn ceremonies of docs/07: creating a passkey (first run, recovery, adding another) and signing in with one.
/// Identity's passkey handler checks the browser's answer (challenge, origin, relying party, signature); the use cases in
/// Application decide who may start a ceremony and what finishing it does. Between the two, the server's memory of the
/// ceremony is a sealed cookie (<see cref="CeremonyCookie"/>).
/// </summary>
public sealed class PasskeyCeremonies(
    IPasskeyHandler<AppUser> passkeys,
    UserManager<AppUser> users,
    IUserClaimsPrincipalFactory<AppUser> claims,
    PlanCeremony.Handler plan,
    CompleteCeremony.Handler complete,
    PlanInvite.Handler planInvite,
    AcceptInvite.Handler acceptInvite,
    IMembershipReader memberships,
    CeremonyCookie ceremonies,
    TimeProvider time)
{
    // ---- starting a creation ------------------------------------------------------------------------------------------

    /// <summary><c>POST /setup</c>: the setup code and the owner's name, then WebAuthn creation options.</summary>
    public async Task<IResult> StartSetupAsync(HttpContext http, SetupRequest body, CancellationToken cancellationToken)
    {
        var planned = await plan.ForSetupAsync(body.Code, body.DisplayName, cancellationToken);
        return planned.IsSuccess
            ? await CreationOptionsAsync(http, planned.Value, planned.Value.DisplayName)
            : AuthProblems.From(planned.Error!);
    }

    /// <summary><c>POST /account/recover</c>: the recovery code from the PC's command line.</summary>
    public async Task<IResult> StartRecoveryAsync(HttpContext http, RecoverRequest body, CancellationToken cancellationToken)
    {
        var planned = await plan.ForRecoveryAsync(body.Code, cancellationToken);
        if (!planned.IsSuccess)
        {
            return AuthProblems.From(planned.Error!);
        }

        var owner = await users.FindByIdAsync(planned.Value.UserId.ToString());
        return owner is null
            ? AuthProblems.From(AccountErrors.NoOwner)
            : await CreationOptionsAsync(http, planned.Value with { DisplayName = owner.DisplayName }, owner.DisplayName);
    }

    /// <summary>
    /// <c>POST /account/passkey/creation-options</c>: with a setup code it is <c>POST /setup</c>; with an invite token it joins
    /// a library (built by ID-3); with neither, a signed-in user adds another passkey, which needs a recent passkey assertion.
    /// </summary>
    public async Task<IResult> CreationOptionsAsync(HttpContext http, PasskeyCreationOptionsRequest body, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(body.SetupCode))
        {
            return await StartSetupAsync(http, new SetupRequest(body.SetupCode, body.DisplayName), cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(body.InviteToken))
        {
            // Joining by invite (ID-3a). A token that is unknown, expired, used or revoked starts nothing, and says nothing about which.
            var invited = await planInvite.HandleAsync(body.InviteToken, body.DisplayName, cancellationToken);
            return invited.IsSuccess
                ? await CreationOptionsAsync(http, invited.Value, invited.Value.DisplayName)
                : Failed(invited.Error!);
        }

        if (AuthCookie.UserId(http.User) is not { } userId)
        {
            return AuthProblems.CeremonyRequired();
        }

        if (!RecentPasskeyFilter.IsRecent(http.User, time.GetUtcNow()))
        {
            return AuthProblems.ReauthRequired();
        }

        var user = await users.FindByIdAsync(userId.ToString());
        return user is null || user.DeletedAt is not null
            ? AuthProblems.NotSignedIn("Sign in again.")
            : await CreationOptionsAsync(http, new CeremonyPlan(CeremonyPurpose.AddPasskey, userId, user.DisplayName, null), user.DisplayName);
    }

    private async Task<IResult> CreationOptionsAsync(HttpContext http, CeremonyPlan planned, string passkeyName)
    {
        var entity = new PasskeyUserEntity { Id = planned.UserId.ToString(), Name = passkeyName, DisplayName = passkeyName };
        var options = await passkeys.MakeCreationOptionsAsync(entity, http);
        ceremonies.Write(http, CeremonyKind.Create, planned, options.AttestationState ?? throw new InvalidOperationException("Identity gave no attestation state."));
        return TypedResults.Ok(new PasskeyOptionsDto(ToElement(options.CreationOptionsJson)));
    }

    // ---- finishing a creation -----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /account/passkey/register</c>: finishes the ceremony the browser started. With no ceremony (no code, token or
    /// recent sign-in first) there is nothing to finish and it is refused.
    /// </summary>
    public async Task<IResult> RegisterAsync(HttpContext http, string? invite, PasskeyRegistrationRequest body, CancellationToken cancellationToken)
    {
        var ceremony = ceremonies.Read(http, CeremonyKind.Create);
        if (ceremony?.Plan is not { } planned)
        {
            return AuthProblems.CeremonyRequired();
        }

        // An invite token in the URL only fits a ceremony that was started by an invite.
        if (invite is not null && planned.Purpose != CeremonyPurpose.Invite)
        {
            return AuthProblems.CeremonyRequired();
        }

        if (body.Credential is not { ValueKind: JsonValueKind.Object } credential)
        {
            return ValidationFailure("credential", "Send the passkey the browser made.");
        }

        if (planned.Purpose == CeremonyPurpose.AddPasskey
            && (AuthCookie.UserId(http.User) != planned.UserId || !RecentPasskeyFilter.IsRecent(http.User, time.GetUtcNow())))
        {
            return AuthProblems.ReauthRequired();
        }

        PasskeyAttestationResult attested;
        try
        {
            attested = await passkeys.PerformAttestationAsync(new PasskeyAttestationContext
            {
                HttpContext = http,
                CredentialJson = credential.GetRawText(),
                AttestationState = ceremony.State,
            });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return ValidationFailure("credential", "That passkey couldn't be read.");
        }

        if (!attested.Succeeded || attested.UserEntity.Id != planned.UserId.ToString())
        {
            return ValidationFailure("credential", "That passkey wasn't accepted.");
        }

        var name = string.IsNullOrWhiteSpace(body.Name) ? null : body.Name.Trim()[..Math.Min(body.Name.Trim().Length, 100)];
        var credentialMade = ToCredential(attested.Passkey, name);
        var finished = planned.Purpose == CeremonyPurpose.Invite
            ? await acceptInvite.HandleAsync(invite, planned, credentialMade, cancellationToken) // ID-3a: spends the invite, makes the user and the membership
            : await complete.HandleAsync(planned, credentialMade, cancellationToken);
        if (!finished.IsSuccess)
        {
            if (planned.Purpose == CeremonyPurpose.Invite)
            {
                ceremonies.Clear(http); // an invite that failed here is gone for this ceremony too
            }

            return Failed(finished.Error!);
        }

        ceremonies.Clear(http);
        if (planned.Purpose != CeremonyPurpose.AddPasskey)
        {
            // First run or recovery: the passkey they just made is the sign-in.
            await SignInAsync(http, finished.Value.UserId);
        }

        return TypedResults.Created("/api/v1/me", finished.Value);
    }

    // ---- signing in ---------------------------------------------------------------------------------------------------

    /// <summary><c>POST /account/passkey/request-options</c>: options for a sign-in with any passkey on the device.</summary>
    public async Task<IResult> RequestOptionsAsync(HttpContext http)
    {
        var options = await passkeys.MakeRequestOptionsAsync(user: null!, http);
        ceremonies.Write(http, CeremonyKind.SignIn, null, options.AssertionState ?? throw new InvalidOperationException("Identity gave no assertion state."));
        return TypedResults.Ok(new PasskeyOptionsDto(ToElement(options.RequestOptionsJson)));
    }

    /// <summary><c>POST /account/login</c>: the passkey's signature over the challenge. A new session, so <c>auth_time</c> is now.</summary>
    public async Task<IResult> LoginAsync(HttpContext http, PasskeyLoginRequest body)
    {
        if (body.Credential is not { ValueKind: JsonValueKind.Object } credential)
        {
            return ValidationFailure("credential", "Send the passkey assertion the browser made.");
        }

        var ceremony = ceremonies.Read(http, CeremonyKind.SignIn);
        if (ceremony is null)
        {
            return AuthProblems.CeremonyRequired();
        }

        PasskeyAssertionResult<AppUser> asserted;
        try
        {
            asserted = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
            {
                HttpContext = http,
                CredentialJson = credential.GetRawText(),
                AssertionState = ceremony.State,
            });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return ValidationFailure("credential", "That passkey couldn't be read.");
        }

        if (!asserted.Succeeded || asserted.User.DeletedAt is not null)
        {
            return AuthProblems.NotSignedIn();
        }

        await users.AddOrUpdatePasskeyAsync(asserted.User, asserted.Passkey); // keeps the signature counter and backup flags current
        ceremonies.Clear(http);
        await SignInAsync(http, asserted.User.Id);
        var libraries = await memberships.GetMembershipsAsync(asserted.User.Id, http.RequestAborted);
        var library = libraries.OrderByDescending(m => m.Role).FirstOrDefault();
        return TypedResults.Ok(new SignedInDto(asserted.User.Id, asserted.User.DisplayName, library?.LibraryId));
    }

    /// <summary><c>POST /account/logout</c>.</summary>
    public static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(AuthCookie.Scheme);
        return TypedResults.NoContent();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A failed ceremony step as a response. An unusable invite is always the one fixed answer (see <see cref="InviteProblems"/>).</summary>
    private static IResult Failed(DomainError error) =>
        error == InviteErrors.Invalid ? InviteProblems.Invalid() : AuthProblems.From(error);

    private async Task SignInAsync(HttpContext http, Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("The user that just signed in is gone.");
        await AuthCookie.SignInAsync(http, await claims.CreateAsync(user), time);
    }

    private static PasskeyCredential ToCredential(UserPasskeyInfo passkey, string? name) =>
        new(
            passkey.CredentialId,
            passkey.PublicKey,
            passkey.SignCount,
            passkey.Transports ?? [],
            passkey.IsUserVerified,
            passkey.IsBackupEligible,
            passkey.IsBackedUp,
            passkey.AttestationObject,
            passkey.ClientDataJson,
            name,
            passkey.CreatedAt);

    private static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem ValidationFailure(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
