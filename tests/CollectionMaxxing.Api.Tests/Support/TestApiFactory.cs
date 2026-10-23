using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>The real API pipeline, pointed at the test Postgres, with the header-based test sign-in.</summary>
public sealed class TestApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development, because the OpenAPI document is only served there.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:collectionmaxxing"] = connectionString,

                // Tests sign in over and over from one address; the rate limit is tested on its own (Auth/RateLimitTests).
                ["Auth:RateLimit:PermitLimit"] = "100000",
                ["Security:Limits:Export:Permits"] = "100000",
                ["Security:Limits:AuthHourly:Permits"] = "100000",
                ["Auth:Setup:IssueOnStartup"] = "false",
                ["Auth:Auth0:Domain"] = "tenant.auth0.test",
                ["Auth:Auth0:Audience"] = "urn:collectionmaxxing:api",
                ["Auth:Auth0:WebClientId"] = "web-client-id",
                ["Auth:Auth0:WebClientSecret"] = "web-client-secret-for-tests",
                ["Auth:PublicOrigin"] = "https://localhost",
            }));

        builder.ConfigureTestServices(services =>
        {
            // No background worker in API tests: a test host with fake providers must not have its jobs claimed by another
            // host's worker (which has the real providers). Tests that need a job run it themselves with JobRunner.
            foreach (var worker in services.Where(d => d.ImplementationType == typeof(JobWorker)).ToList())
            {
                services.Remove(worker);
            }

            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // GET /account/login still goes to "Auth0": a fixed configuration, so nothing is fetched over the network.
            services.Configure<OpenIdConnectOptions>(CollectionMaxxing.Api.Auth.Auth0Setup.OidcScheme, options =>
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://tenant.auth0.test/",
                    AuthorizationEndpoint = "https://tenant.auth0.test/authorize",
                    TokenEndpoint = "https://tenant.auth0.test/oauth/token",
                });
        });
    }
}
