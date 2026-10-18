using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>
/// docs/08 "Logging and monitoring": a bearer token, a cookie, a one-time code, a signed-link token, an email address and a photo never reach a log.
/// Everything is captured, at Trace level and from every category, while requests carry canary values in each place a secret can travel.
/// (Scan codes and provider keys have their own locked test in Scanning/ScanLogHygieneTests; the sign-in flow in Auth/LogHygieneTests.)
/// </summary>
public class LogRedactionTests(ApiFixture api) : ApiTest(api)
{
    private const string Bearer = "canary-bearer-6d1f0c";
    private const string Cookie = "canary-cookie-93ab77";
    private const string Token = "canary-linktoken-51e2d8";
    private const string Code = "canary-authcode-a0c4f9";
    private const string Email = "canary.person@example.test";
    private const string Photo = "canary-photo-bytes-77cd01";

    [Fact]
    public async Task No_secret_travelling_in_a_header_a_query_or_a_body_appears_in_any_log_line()
    {
        var logs = new CapturingLogger();
        using var factory = Api.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Trace" }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        var library = await CreateLibraryAsync();
        using var signedIn = factory.CreateClient();
        signedIn.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, library.UserWithRole(LibraryRole.Owner).ToString());
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        anonymous.DefaultRequestHeaders.Add("Cookie", $"__Host-cm={Cookie}");
        var session = Guid.NewGuid();

        // Signed-link download, an unknown link, a token endpoint with a code, an invite preview, a photo with the wrong shape.
        foreach (var client in new[] { signedIn, anonymous })
        {
            (await client.GetAsync($"/api/v1/me/export/{Guid.NewGuid()}/download?token={Token}&email={Email}", TestContext.Current.CancellationToken)).Dispose();
            (await client.GetAsync($"/api/v1/invites/{Token}?code={Code}", TestContext.Current.CancellationToken)).Dispose();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = Code,
                ["refresh_token"] = Token,
                ["username"] = Email,
            });
            (await client.PostAsync("/connect/token", form, TestContext.Current.CancellationToken)).Dispose();
            using var upload = new MultipartFormDataContent { { new ByteArrayContent(Encoding.ASCII.GetBytes(Photo)), "file", $"{Email}.jpg" } };
            (await client.PostAsync($"/api/v1/scan-sessions/{session}/candidates/{Guid.NewGuid()}/photo", upload, TestContext.Current.CancellationToken)).Dispose();
        }

        logs.Lines.ShouldContain(line => line.Contains("/api/v1/invites/{token} answered 404"), "the request line names the route, not the address");
        foreach (var secret in new[] { Bearer, Cookie, Token, Code, Email, Photo })
        {
            logs.Lines.Where(line => line.Contains(secret, StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"a log line contains the canary '{secret}'");
        }
    }

    private sealed class CapturingLogger : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Capture(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var structured = state is IEnumerable<KeyValuePair<string, object?>> values ? string.Join(' ', values.Select(v => $"{v.Key}={v.Value}")) : string.Empty;
                lines.Enqueue($"{category} {formatter(state, exception)} {structured} {exception}");
            }
        }
    }
}
