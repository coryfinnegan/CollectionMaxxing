using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// Errors in Production (docs/08, OWASP API8): every 4xx and 5xx is RFC 9457 problem+json, and none carries a stack trace, an exception type,
/// an exception message, a file path or SQL. The 500s are provoked by a service that throws an exception whose message is full of things that
/// must never reach a client.
/// </summary>
public sealed class ProductionErrorTests(ApiFixture api) : IAsyncLifetime
{
    private const string Poison = "SELECT password_hash FROM users WHERE name = 'hunter2' -- Npgsql.PostgresException C:\\src\\Secret.cs:line 42";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-prod-errors-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private TestLibrary _library = null!;

    public async ValueTask InitializeAsync()
    {
        await api.Db.ResetAsync();
        _library = await TestData.CreateLibraryAsync(api.Db);
        Directory.CreateDirectory(_root);
        WritePfx("signing.pfx");
        WritePfx("encryption.pfx");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:collectionmaxxing"] = api.Db.ConnectionString,
                    ["Auth:SecretsPath"] = Path.Combine(_root, "secrets"),
                    ["Auth:KeyRing:Path"] = Path.Combine(_root, "keys"),
                    ["Auth:Keys:SigningCertificatePath"] = Path.Combine(_root, "signing.pfx"),
                    ["Auth:Keys:EncryptionCertificatePath"] = Path.Combine(_root, "encryption.pfx"),
                    ["Auth:Setup:IssueOnStartup"] = "false",
                    ["Auth:RateLimit:PermitLimit"] = "100000",
                }));
            builder.ConfigureTestServices(services =>
            {
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(JobWorker)).ToList())
                {
                    services.Remove(worker);
                }

                services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                // A reader that fails, and says too much about why, for any caller that sends the poison header.
                services.RemoveAll<IMembershipReader>();
                services.AddScoped<IMembershipReader, BrokenOnRequestReader>();
                services.AddHttpContextAccessor();
            });
        });
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private void WritePfx(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=cm-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(Path.Combine(_root, name), certificate.Export(X509ContentType.Pfx));
    }

    private HttpClient Client(Guid? user = null, bool broken = false)
    {
        var client = _factory.CreateClient();
        if (user is { } id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, id.ToString());
        }

        if (broken)
        {
            client.DefaultRequestHeaders.Add(BrokenOnRequestReader.Header, "1");
        }

        return client;
    }

    private static readonly string[] Leaks =
    [
        "Exception", "   at ", ".cs", "line 42", "stackTrace", "StackTrace", "Npgsql", "SELECT", "hunter2", "password_hash", "InvalidOperation", "System.", "Microsoft.",
        "CollectionMaxxing.", "\\src\\", "innerException", "exceptionDetails",
    ];

    private static async Task ShouldBeCleanProblemAsync(HttpResponseMessage response, int status, string because)
    {
        var body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(status, $"{because}: {body}");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", $"{because}: {body}");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(status, because);
        foreach (var leak in Leaks)
        {
            body.ShouldNotContain(leak, Case.Sensitive, $"{because}: the answer leaks '{leak}': {body}");
        }
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_500_problem_with_nothing_about_the_exception_in_it()
    {
        _factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName.ShouldBe("Production");
        using var client = Client(_library.OwnerId, broken: true);

        foreach (var path in new[] { "/api/v1/me", $"/api/v1/libraries/{_library.Id}/items", "/api/v1/libraries", $"/api/v1/scan-sessions/{Guid.NewGuid()}" })
        {
            using var response = await client.GetAsync(path);
            await ShouldBeCleanProblemAsync(response, 500, path);
        }
    }

    [Fact]
    public async Task A_500_on_a_write_with_a_body_is_just_as_clean()
    {
        using var client = Client(_library.OwnerId, broken: true);
        using var content = new StringContent("""{ "name": "x" }""", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync($"/api/v1/libraries/{_library.Id}/tags", content);

        await ShouldBeCleanProblemAsync(response, 500, "POST tags");
    }

    [Fact]
    public async Task The_ordinary_4xx_answers_are_clean_problem_documents_too()
    {
        using var owner = Client(_library.OwnerId);
        using var viewer = Client(_library.ViewerId);
        using var stranger = Client(_library.OutsiderId);
        using var anonymous = Client();
        var tags = $"/api/v1/libraries/{_library.Id}/tags";

        // 400: a body that is not JSON, and one that is JSON of the wrong shape
        using (var response = await owner.PostAsync(tags, new StringContent("{ not json", Encoding.UTF8, "application/json")))
        {
            await ShouldBeCleanProblemAsync(response, 400, "malformed JSON");
        }

        using (var response = await owner.PostAsync(tags, new StringContent("""{ "name": 12 }""", Encoding.UTF8, "application/json")))
        {
            await ShouldBeCleanProblemAsync(response, 400, "wrong shape");
        }

        using (var response = await owner.PostAsync($"/api/v1/libraries/not-a-guid/tags", new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            ((int)response.StatusCode).ShouldBeInRange(400, 404);
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("Exception");
        }

        // 401, 403, 404, 405, 415
        using (var response = await anonymous.GetAsync("/api/v1/me"))
        {
            await ShouldBeCleanProblemAsync(response, 401, "anonymous");
        }

        using (var response = await viewer.PostAsync(tags, new StringContent("""{ "name": "x" }""", Encoding.UTF8, "application/json")))
        {
            await ShouldBeCleanProblemAsync(response, 403, "viewer write");
        }

        using (var response = await stranger.GetAsync($"/api/v1/libraries/{_library.Id}/items"))
        {
            await ShouldBeCleanProblemAsync(response, 404, "not a member");
        }

        using (var response = await owner.GetAsync("/api/v1/does-not-exist"))
        {
            await ShouldBeCleanProblemAsync(response, 404, "unknown route");
        }

        using (var response = await owner.DeleteAsync("/api/v1/me/export"))
        {
            ((int)response.StatusCode).ShouldBe(405);
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("Exception");
        }

        using (var response = await owner.PostAsync(tags, new StringContent("name=x", Encoding.UTF8, "text/plain")))
        {
            ((int)response.StatusCode).ShouldBeInRange(400, 415);
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("Exception");
        }
    }

    [Fact]
    public async Task The_developer_pages_do_not_exist_in_production()
    {
        using var owner = Client(_library.OwnerId);
        using var anonymous = Client();

        foreach (var path in new[] { "/openapi/v1.json", "/openapi/v1.yaml", "/scalar", "/scalar/v1", "/swagger", "/swagger/index.html", "/developer-exception-page" })
        {
            foreach (var client in new[] { owner, anonymous })
            {
                using var response = await client.GetAsync(path);
                ((int)response.StatusCode).ShouldBeInRange(401, 404, path);
                response.StatusCode.ShouldNotBe(HttpStatusCode.OK, path);
            }
        }
    }

    [Fact]
    public async Task A_malformed_token_form_is_a_client_error_and_says_nothing_about_the_server()
    {
        using var client = Client();
        using var content = new StringContent("grant_type=refresh_token%00", Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await client.PostAsync("/connect/token", content);

        await ShouldBeCleanProblemAsync(response, 400, "NUL in a form");
    }

    /// <summary>Throws, with a message that is full of secrets, when the request carries the header.</summary>
    private sealed class BrokenOnRequestReader(IHttpContextAccessor http, IServiceProvider services) : IMembershipReader
    {
        public const string Header = "X-Break";

        public Task<IReadOnlyList<Application.Common.Membership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken)
        {
            if (http.HttpContext?.Request.Headers.ContainsKey(Header) == true)
            {
                throw new InvalidOperationException(Poison, new TimeoutException(Poison));
            }

            return ActivatorUtilities.CreateInstance<CollectionMaxxing.Infrastructure.Persistence.MembershipReader>(services).GetMembershipsAsync(userId, cancellationToken);
        }
    }
}
