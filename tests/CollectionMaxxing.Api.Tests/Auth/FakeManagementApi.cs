using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>
/// A fake of Auth0's Management API on loopback (WireMock): <c>POST /oauth/token</c> for the machine-to-machine token, then
/// <c>DELETE /api/v2/grants</c> and <c>DELETE /api/v2/users/{id}</c>. The API still believes it talks to <c>https://tenant.auth0.test/</c>;
/// <see cref="Configure"/> sends those requests here instead. Starts healthy; <see cref="Failing"/> and <see cref="Gone"/> change what
/// the two DELETE routes answer.
/// </summary>
public sealed class FakeManagementApi : IDisposable
{
    public const string ClientId = "management-client-id";
    public const string ClientSecret = "management-client-secret-for-tests";

    private readonly WireMockServer _server = LoopbackWireMock.Start();

    public FakeManagementApi() => Healthy();

    /// <summary>The API's settings for the Management client (the names of issue #226).</summary>
    public static Dictionary<string, string?> Settings => new()
    {
        ["Auth:Auth0:ManagementClientId"] = ClientId,
        ["Auth:Auth0:ManagementClientSecret"] = ClientSecret,
    };

    /// <summary>The token lives this many seconds, so a test can step the clock past "five minutes before it expires".</summary>
    public int TokenLifetimeSeconds { get; set; } = 3600;

    /// <summary>Sends the API's Management requests (to <c>https://tenant.auth0.test/</c>) to this fake.</summary>
    public void Configure(IServiceCollection services)
    {
        var port = new Uri(_server.Urls[0]).Port;
        services.AddHttpClient(Auth0Management.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new ToLoopback(port) { InnerHandler = new HttpClientHandler() });
    }

    /// <summary>Everything works: the token, and 204 for both DELETE routes.</summary>
    public void Healthy() => Answer(204);

    /// <summary>The identity is already gone: 404 for both DELETE routes (which counts as done).</summary>
    public void Gone() => Answer(404);

    /// <summary>The DELETE routes answer <paramref name="status"/> (the token still works).</summary>
    public void Failing(int status = 500) => Answer(status);

    /// <summary>The requests received so far for <paramref name="method"/> and a path starting with <paramref name="pathPrefix"/>.</summary>
    public IReadOnlyList<ReceivedCall> Calls(string method, string pathPrefix) =>
        [.. _server.LogEntries
            .Select(e => e.RequestMessage)
            .Where(r => r is not null && string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase) && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
            .Select(r => new ReceivedCall(
                r!.Path,
                Uri.UnescapeDataString(r.Url),
                r.Headers is not null && r.Headers.TryGetValue("Authorization", out var values) ? values.FirstOrDefault() : null,
                r.Body))];

    public IReadOnlyList<ReceivedCall> GrantRevocations => Calls("DELETE", "/api/v2/grants");

    public IReadOnlyList<ReceivedCall> UserDeletions => Calls("DELETE", "/api/v2/users/");

    public IReadOnlyList<ReceivedCall> TokenRequests => Calls("POST", "/oauth/token");

    public void Dispose() => _server.Stop();

    private void Answer(int status)
    {
        _server.ResetMappings(); // the log of requests received stays
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new { access_token = "m2m-token", token_type = "Bearer", expires_in = TokenLifetimeSeconds })));
        _server.Given(Request.Create().WithPath("/api/v2/grants").UsingDelete()).RespondWith(Response.Create().WithStatusCode(status));
        _server.Given(Request.Create().WithPath("/api/v2/users/*").UsingDelete()).RespondWith(Response.Create().WithStatusCode(status));
    }

    /// <summary>A request the fake received. <c>Url</c> is unescaped so a test can look for a subject in it.</summary>
    public sealed record ReceivedCall(string Path, string Url, string? Authorization, string? Body);

    private sealed class ToLoopback(int port) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = Uri.UriSchemeHttp, Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
