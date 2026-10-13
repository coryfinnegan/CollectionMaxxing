using System.Globalization;

namespace CollectionMaxxing.Api.Auth;

/// <summary>Endpoint metadata: this action needs a passkey assertion from the last five minutes (docs/07, "Re-auth").</summary>
public sealed record RequireRecentPasskeyMetadata;

/// <summary>
/// Passes the request on only if the session's <c>auth_time</c> is within <see cref="MaxAge"/>. Otherwise 401 Problem Details of
/// type <c>reauth_required</c> with <c>reauth: true</c>, which the web app answers by asking for the passkey again.
/// A fresh <c>POST /account/login</c> makes a new session whose <c>auth_time</c> is now.
/// </summary>
public sealed class RecentPasskeyFilter(TimeProvider time) : IEndpointFilter
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        IsRecent(context.HttpContext.User, time.GetUtcNow())
            ? next(context)
            : ValueTask.FromResult<object?>(AuthProblems.ReauthRequired());

    public static bool IsRecent(System.Security.Claims.ClaimsPrincipal user, DateTimeOffset now)
    {
        var claim = user.FindFirst(AuthCookie.AuthTimeClaim)?.Value;
        if (!long.TryParse(claim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return false; // no assertion on record (for example a bearer token): ask for one
        }

        return now - DateTimeOffset.FromUnixTimeSeconds(seconds) <= MaxAge;
    }
}

public static class RecentPasskeyExtensions
{
    /// <summary>Requires a passkey assertion from the last five minutes. Put it after the authorization the endpoint already has.</summary>
    public static TBuilder RequireRecentPasskey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequireRecentPasskeyMetadata());
        builder.AddEndpointFilter<TBuilder, RecentPasskeyFilter>();
        return builder;
    }
}
