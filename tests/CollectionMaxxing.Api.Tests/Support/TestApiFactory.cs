using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
                ["Auth:Setup:IssueOnStartup"] = "false",
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
        });
    }
}
