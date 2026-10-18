using System.Diagnostics;

namespace CollectionMaxxing.Api.Security;

/// <summary>
/// One log line per request that can't carry a secret. The framework's own lines print the whole address, and in this API secrets travel in the
/// address (a signed export link's <c>token</c> query value, an invite token in the path, a pairing code), so those categories are turned down to
/// Warning (see <see cref="SecurityHeaders.AddSecurityHeaders"/>) and this middleware logs the method, the route <em>template</em> (never the
/// values), the status and the time instead. Headers, cookies and bodies are never logged.
/// </summary>
public sealed partial class RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var template = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Completed(context.Request.Method, template, context.Response.StatusCode, elapsed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Method} {Route} answered {Status} in {Milliseconds:0.0} ms")]
    private partial void Completed(string method, string route, int status, double milliseconds);
}
