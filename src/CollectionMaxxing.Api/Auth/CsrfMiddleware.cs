namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Every write that comes with the session cookie must also carry <c>X-CSRF: 1</c> (docs/07). SameSite=Strict already keeps
/// other sites from sending the cookie; the header is the second lock. Reads, and callers that use a bearer token instead of
/// the cookie (the iPhone), are not affected.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CSRF";
    public const string HeaderValue = "1";

    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get, HttpMethods.Head, HttpMethods.Options, HttpMethods.Trace,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (NeedsHeader(context.Request) && context.Request.Headers[HeaderName] != HeaderValue)
        {
            await AuthProblems.CsrfRequired().ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool NeedsHeader(HttpRequest request) =>
        !SafeMethods.Contains(request.Method)
        && request.Cookies.ContainsKey(AuthCookie.Name)
        && !IsBearer(request);

    private static bool IsBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}
