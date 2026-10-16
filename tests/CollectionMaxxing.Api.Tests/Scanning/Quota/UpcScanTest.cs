using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CollectionMaxxing.Api.Tests.Scanning.Quota;

/// <summary>
/// Scanning with the app's real UPCitemdb adapter and daily quota, pointed at a WireMock server, on a clock the test moves.
/// The database is shared with the other tests, so the clock starts on a random day a few months ahead (the quota is counted per
/// UTC day, and the app's signing keys are valid for years, so not too far), and the lookup answers only for the codes this test
/// stubbed: any other code (a leftover from another test) is
/// "unavailable" without a request, so it neither spends this test's quota nor shows up in the request count.
/// </summary>
public abstract class UpcScanTest : ScanTest
{
    private const string LookupPath = "/prod/trial/lookup";

    private readonly HashSet<string> _mine = [];

    protected UpcScanTest(ApiFixture api, int dailyLimit)
        : base(api)
    {
        Upc = LoopbackWireMock.Start();
        Clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(Random.Shared.Next(2, 360)).AddHours(10));
        Options.ResolveBudget = TimeSpan.FromSeconds(5);
        Settings["Providers:UpcItemDb:BaseUrl"] = Upc.Url + "/";
        Settings["Providers:UpcItemDb:DailyLimit"] = dailyLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Settings["Providers:UpcItemDb:RequestsPerSecond"] = "1000000000";
        ConfigureServices = services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IProductLookup>();
            services.AddScoped<UpcProductLookup>();
            services.AddScoped<IProductLookup>(sp => new MineOnly(sp.GetRequiredService<UpcProductLookup>(), _mine));
        };
    }

    protected WireMockServer Upc { get; }

    protected FakeTimeProvider Clock { get; }

    /// <summary>How many lookups reached the (fake) UPCitemdb.</summary>
    protected int UpcRequests => Upc.LogEntries.Count(e => e.RequestMessage?.Path == LookupPath);

    /// <summary>Makes UPCitemdb know <paramref name="upc"/> (a UPC-A) as a game called <paramref name="title"/>, and the game provider know the game.</summary>
    protected void KnowProduct(string upc, string title)
    {
        Identifier.TryParse(upc, out var id).ShouldBeTrue();
        _mine.Add(id!.Value);
        var body = JsonSerializer.Serialize(new
        {
            code = "OK",
            total = 1,
            offset = 0,
            items = new[]
            {
                new
                {
                    ean = id.Value,
                    title = $"{title} - Nintendo 64 (Cartridge Only)",
                    upc,
                    brand = "Nintendo",
                    category = "Electronics > Video Games > Video Game Software",
                    images = Array.Empty<string>(),
                },
            },
        });
        Upc.Given(Request.Create().WithPath(LookupPath).WithParam("upc", upc).UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(body));
        Games.Results.Add(ScanData.Game(title, "Nintendo 64", 1996, $"game-{upc}"));
    }

    public override async ValueTask DisposeAsync()
    {
        Upc.Stop();
        Upc.Dispose();
        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }

    private sealed class MineOnly(UpcProductLookup real, HashSet<string> mine) : IProductLookup
    {
        public Task<ProductLookupResult> LookupAsync(Identifier id, CancellationToken cancellationToken) =>
            mine.Contains(id.Value) ? real.LookupAsync(id, cancellationToken) : Task.FromResult(ProductLookupResult.Unavailable);
    }
}
