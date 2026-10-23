using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>
/// The platform role behind <c>/api/admin/*</c> (the iPhone scanner evals and the job list). It is given and taken away on the host only
/// (<c>cm-admin dev-tools grant|revoke &lt;email&gt;</c>, ADR-027): nothing in the app grants it, and it is not a library role, so it opens
/// no library.
/// </summary>
public static class PlatformRoles
{
    /// <summary>The name of the role in the identity store. <c>/api/admin/*</c> requires it.</summary>
    public const string DevTools = "admin";
}

/// <summary>The platform roles of accounts (implemented by Infrastructure, on ASP.NET Core Identity's roles).</summary>
public interface IPlatformRoles
{
    /// <summary>Gives the account the developer-tools role, making the role first if it doesn't exist yet. Giving it twice is fine.</summary>
    Task GrantDevToolsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Takes the role away. Returns false, and changes nothing, when the account didn't have it.</summary>
    Task<bool> RevokeDevToolsAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Why a developer-tools command did nothing.</summary>
public static class DevToolsErrors
{
    public static readonly DomainError EmailRequired =
        new(ErrorKind.Validation, "dev_tools.email_required", "Name the account by its email address.", "email");

    public static readonly DomainError EmailUnknown =
        new(ErrorKind.NotFound, "dev_tools.email_unknown", "No account has that email address. Nothing was changed.");

    public static readonly DomainError EmailAmbiguous =
        new(ErrorKind.Conflict, "dev_tools.email_ambiguous", "More than one account has that email address. Nothing was changed.");
}

/// <summary>Use case: <c>cm-admin dev-tools grant|revoke &lt;email&gt;</c>. A support tool for the person at the host.</summary>
public static class SetDevTools
{
    public sealed class Handler(IUserDirectory users, IPlatformRoles roles, IWebSessions webSessions)
    {
        /// <summary>
        /// Gives (<paramref name="grant"/>) or takes away the developer-tools role of the one account with <paramref name="email"/> (its own
        /// email or one of its sign-ins', ignoring case) and returns its display name. An unknown email, or one that two accounts share, is an
        /// error and nothing changes. Revoking a role the account had also signs its browsers out, because a browser's sign-in carries the
        /// roles it was made with; granting needs a new sign-in to show.
        /// </summary>
        public async Task<Result<string>> HandleAsync(bool grant, string? email, CancellationToken cancellationToken)
        {
            var address = email?.Trim();
            if (string.IsNullOrEmpty(address))
            {
                return DevToolsErrors.EmailRequired;
            }

            var accounts = await users.FindActiveByEmailAsync(address, cancellationToken);
            if (accounts.Count == 0)
            {
                return DevToolsErrors.EmailUnknown;
            }

            if (accounts.Count > 1)
            {
                return DevToolsErrors.EmailAmbiguous;
            }

            var userId = accounts[0];
            var name = (await users.GetProfileAsync(userId, cancellationToken))?.DisplayName ?? "the account";
            if (grant)
            {
                await roles.GrantDevToolsAsync(userId, cancellationToken);
            }
            else
            {
                if (await roles.RevokeDevToolsAsync(userId, cancellationToken))
                {
                    await webSessions.RevokeAllAsync(userId, cancellationToken);
                }
            }

            return name;
        }
    }
}
