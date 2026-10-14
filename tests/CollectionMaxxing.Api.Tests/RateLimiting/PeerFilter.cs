using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace CollectionMaxxing.Api.Tests.RateLimiting;

/// <summary>
/// The test server has no real network peer. This lets a test choose who the "connection" comes from with the
/// <c>X-Test-Peer</c> header (before anything else in the pipeline sees the request), the way a real socket would say it.
/// </summary>
public sealed class PeerFilter : IStartupFilter
{
    public const string Header = "X-Test-Peer";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, forward) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var peer) && IPAddress.TryParse(peer.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return forward(context);
        });
        next(app);
    };
}
