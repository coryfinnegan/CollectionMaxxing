using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Onboarding;

/// <summary>
/// The phone side of the eval server (#523): the Bearer [REDACTED] carries the developer-tools role when the account holds it, so a
/// granted phone reaches the eval routes (the EvalsRow probe, the golden download, the run submit) and an ungranted phone is still
/// 403. Real sign-in throughout; the golden set is generated, never the owner's photos. The runs below report an engine failure, so
/// nothing grounds through the providers.
/// </summary>
public class PhoneEvalsTests(ApiFixture api) : OnboardingTest(api)
{
    private const string Base = "/api/admin/evals";

    private static readonly SyntheticGolden.SingleItem CartTruth =
        new("Secret Cart Title", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    protected SyntheticGolden Golden { get; } = new();

    protected override Dictionary<string, string?>? MoreSettings => new()
    {
        ["Evals:Enabled"] = "true",
        ["Evals:GoldenPath"] = Golden.Golden,
        ["Evals:ResultsPath"] = Golden.Results,
    };

    public override async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await base.DisposeAsync();
        Golden.Dispose();
    }

    [Fact]
    public async Task Granted_phone_reaches_every_eval_route()
    {
        Golden.AddSingle("c1", CartTruth);
        var (ana, _) = await NewAccountAsync("Ana");
        var phone = App.NewPhone(ana.Identity.Subject); // the token is minted before the grant: no re-sign-in
        (await phone.GetAsync($"{Base}/golden")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await RunAsync("grant", ana.Identity.Email)).ExitCode.ShouldBe(0);

        using var list = await phone.GetAsync($"{Base}/golden");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        (await list.Content.ReadAsStringAsync()).ShouldContain("c1");

        using var image = await phone.GetAsync($"{Base}/golden/c1/image");
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");

        using var run = await phone.PostAsync($"{Base}/runs", RunBody("c1"));
        run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());

        using var latest = await phone.GetAsync($"{Base}/runs/latest");
        latest.StatusCode.ShouldBe(HttpStatusCode.OK, await latest.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Phone_and_browser_without_the_grant_get_403_on_every_eval_route()
    {
        Golden.AddSingle("c1", CartTruth);
        var (ana, _) = await NewAccountAsync("Ana");
        var phone = App.NewPhone(ana.Identity.Subject);

        using var golden = await phone.GetAsync($"{Base}/golden");
        golden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var image = await phone.GetAsync($"{Base}/golden/c1/image");
        image.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var run = await phone.PostAsync($"{Base}/runs", RunBody("c1"));
        run.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var latest = await phone.GetAsync($"{Base}/runs/latest");
        latest.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await ana.GetAsync($"{Base}/golden")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ana.GetAsync($"{Base}/runs/latest")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Revoke_takes_the_phone_off_the_eval_routes_on_its_next_call()
    {
        Golden.AddSingle("c1", CartTruth);
        var (ana, _) = await NewAccountAsync("Ana");
        var phone = App.NewPhone(ana.Identity.Subject);
        (await RunAsync("grant", ana.Identity.Email)).ExitCode.ShouldBe(0);
        (await phone.GetAsync($"{Base}/golden")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RunAsync("revoke", ana.Identity.Email)).ExitCode.ShouldBe(0);

        (await phone.GetAsync($"{Base}/golden")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string? action, string? email)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await AdminCommands.RunDevToolsAsync(App.Services, action, email, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>A run whose photo reports an engine failure: valid, but nothing grounds, so no provider is touched.</summary>
    private static StringContent RunBody(string photoId) => new(
        JsonSerializer.Serialize(new
        {
            device = new { model = "iPhone18,1", osVersion = "26.0", appVersion = "0.3.0" },
            configVersion = "cfg-test-1",
            results = new[] { new { photoId, engine = "apple_model", outcome = "error", latencyMs = 1500, thermalState = "nominal" } },
        }),
        Encoding.UTF8,
        "application/json");
}
