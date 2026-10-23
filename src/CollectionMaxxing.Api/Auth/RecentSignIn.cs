using System.Globalization;
using System.Security.Claims;
using CollectionMaxxing.Application.Accounts;

namespace CollectionMaxxing.Api.Auth;

/// <summary>Endpoint metadata: this action needs a sign-in from the last five minutes (docs/14, "Recent sign-in").</summary>
public sealed record RequireRecentSignInMetadata;

/// <summary>
/// Passes the request on only if the session's <c>auth_time</c> (from the Auth0 ID token) is within <see cref="MaxAge"/>. Otherwise 401
/// Problem Details of type <c>reauth_required</c> with <c>reauth: true</c>, which the web app answers by sending the person through
/// <c>/account/login?reauth=1</c>. A bearer token carries no <c>auth_time</c> of ours, so it never counts as recent.
/// </summary>
public sealed class RecentSignInFilter(TimeProvider time) : IEndpointFilter
{
    public static readonly TimeSpan MaxAge = CompleteSignIn.RecentSignIn;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        IsRecent(context.HttpContext.User, time.GetUtcNow())
            ? next(context)
            : ValueTask.FromResult<object?>(AuthProblems.ReauthRequired());

    public static bool IsRecent(ClaimsPrincipal user, DateTimeOffset now) => CompleteSignIn.IsRecent(AuthTime(user), now);

    /// <summary>When the person behind this session last signed in at Auth0, or null.</summary>
    public static DateTimeOffset? AuthTime(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst(AuthCookie.AuthTimeClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}

public static class RecentSignInExtensions
{
    /// <summary>Requires a sign-in from the last five minutes. Put it after the authorization the endpoint already has.</summary>
    public static TBuilder RequireRecentSignIn<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequireRecentSignInMetadata());
        builder.AddEndpointFilter<TBuilder, RecentSignInFilter>();
        return builder;
    }
}
