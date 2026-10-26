using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.UpcItemDb;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

public class UpcItemDbProviderTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Path = "/prod/trial/lookup";

    private static void Stub(ProviderHost host, string fixture) =>
        host.Server.Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture(fixture)));

    /// <summary>A valid UPC-A for any number, so a test can look up as many different codes as it likes.</summary>
    private static Identifier Code(int n)
    {
        var body = $"0{n:D10}";
        var sum = body.Select((c, i) => (c - '0') * (i % 2 == 0 ? 3 : 1)).Sum();
        Identifier.TryParse(body + ((10 - (sum % 10)) % 10), out var id).ShouldBeTrue();
        return id!;
    }

    [Fact]
    public async Task Turns_a_game_barcode_into_a_cleaned_release()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "upcitemdb-game.json");

        var release = await host.Get<UpcItemDbProvider>().LookupAsync(ProviderCase.Upc, default);

        release.ShouldNotBeNull();
        release.Provider.ShouldBe("upcitemdb");
        release.Title.ShouldBe("Super Mario 64");
        release.Kind.ShouldBe(WorkKind.Game);
        release.Platform.ShouldNotBeNull().ShouldBe("N64");
        release.Publisher.ShouldBe("Nintendo");
        release.CoverUrl.ShouldBeNull(); // a retailer's image host is not on the allow-list
        release.Identifiers.ShouldContain(ProviderCase.Upc);
    }

    [Fact]
    public async Task A_game_with_no_category_is_still_a_game_when_the_name_carries_its_platform()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "upcitemdb-game-nocategory.json");

        Identifier.TryParse("045496905934", out var code).ShouldBeTrue();
        var release = await host.Get<UpcItemDbProvider>().LookupAsync(code!, default);

        release.ShouldNotBeNull();
        release.Title.ShouldBe("Hyrule Warriors: Age of Imprisonment");
        release.Kind.ShouldBe(WorkKind.Game);
        release.Platform.ShouldNotBeNull().ShouldBe("Switch 2");
        release.Publisher.ShouldBe("Nintendo");
        release.CoverUrl.ShouldBeNull(); // a retailer's image host is not on the allow-list
        release.Identifiers.ShouldContain(code!);
    }

    [Fact]
    public async Task Classifies_a_dvd_from_its_category()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "upcitemdb-dvd.json");

        Identifier.TryParse("883929106233", out var dvd).ShouldBeTrue();
        var release = await host.Get<UpcItemDbProvider>().LookupAsync(dvd!, default);

        release.ShouldNotBeNull();
        release.Title.ShouldBe("Casablanca");
        release.Kind.ShouldBe(WorkKind.Film);
        release.Format.ShouldBe(ReleaseFormat.Dvd);
    }

    [Fact]
    public async Task Sends_no_key_header_in_free_mode_and_uses_the_trial_endpoint()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "upcitemdb-game.json");

        await host.Get<UpcItemDbProvider>().LookupAsync(ProviderCase.Upc, default);

        var request = host.Server.LogEntries.Single().RequestMessage!;
        request.Path.ShouldBe(Path);
        request.Headers!.Keys.ShouldNotContain(k => k.Equals("user_key", StringComparison.OrdinalIgnoreCase));
        request.Headers.Keys.ShouldNotContain(k => k.Equals("key_type", StringComparison.OrdinalIgnoreCase));
        request.Query!["upc"].ToString().ShouldBe("045496830434");
    }

    [Fact]
    public async Task Uses_the_paid_endpoint_and_key_header_only_when_a_key_is_configured()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:UpcItemDb:UserKey"] = "paid-test-key" });
        host.Server.Given(Request.Create().WithPath("/prod/v1/lookup").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture("upcitemdb-game.json")));

        (await host.Get<UpcItemDbProvider>().LookupAsync(ProviderCase.Upc, default)).ShouldNotBeNull();

        host.Server.LogEntries.Single().RequestMessage!.Headers!["user_key"].ToString().ShouldBe("paid-test-key");
    }

    [Fact]
    public async Task Spends_the_daily_allowance_and_then_says_the_quota_is_exhausted_without_sending()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true, settings: new() { ["Providers:UpcItemDb:DailyLimit"] = "3" });
        Stub(host, "upcitemdb-empty.json");
        var provider = host.Get<UpcItemDbProvider>();

        for (var n = 1; n <= 3; n++)
        {
            (await provider.LookupWithStatusAsync(Code(n), default)).Status.ShouldBe(UpcLookupStatus.NotFound);
        }

        var fourth = await provider.LookupWithStatusAsync(Code(4), default);

        fourth.Status.ShouldBe(UpcLookupStatus.QuotaExhausted);
        fourth.Release.ShouldBeNull();
        host.Requests(Path).ShouldBe(3); // the fourth lookup never left the building
        (await provider.LookupAsync(Code(5), default)).ShouldBeNull(); // the port's shape: just "no release"
    }

    [Fact]
    public void The_free_allowance_is_100_a_day() => new UpcItemDbOptions().DailyLimit.ShouldBe(100);

    [Fact]
    public async Task A_cached_answer_does_not_spend_the_allowance()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true, settings: new() { ["Providers:UpcItemDb:DailyLimit"] = "1" });
        Stub(host, "upcitemdb-game.json");
        var provider = host.Get<UpcItemDbProvider>();

        (await provider.LookupWithStatusAsync(ProviderCase.Upc, default)).Status.ShouldBe(UpcLookupStatus.Found);
        (await provider.LookupWithStatusAsync(ProviderCase.Upc, default)).Status.ShouldBe(UpcLookupStatus.Found);
        (await provider.LookupWithStatusAsync(ProviderCase.Upc, default)).Status.ShouldBe(UpcLookupStatus.Found);

        host.Requests(Path).ShouldBe(1);
    }

    [Fact]
    public async Task The_count_survives_a_restart_and_resets_the_next_utc_day()
    {
        var settings = new Dictionary<string, string?> { ["Providers:UpcItemDb:DailyLimit"] = "2" };
        using (var first = ProviderHost.Create(Db, fakeTime: true, settings))
        {
            Stub(first, "upcitemdb-empty.json");
            await first.Get<UpcItemDbProvider>().LookupWithStatusAsync(Code(1), default);
            await first.Get<UpcItemDbProvider>().LookupWithStatusAsync(Code(2), default);
        }

        // A new process: new services, new mock server, same database. Same fake day, shortly before midnight UTC.
        using var restarted = ProviderHost.Create(Db, fakeTime: true, settings);
        restarted.Fake.SetUtcNow(new DateTimeOffset(2026, 3, 1, 23, 59, 59, TimeSpan.Zero));
        Stub(restarted, "upcitemdb-empty.json");
        var provider = restarted.Get<UpcItemDbProvider>();

        (await provider.LookupWithStatusAsync(Code(3), default)).Status.ShouldBe(UpcLookupStatus.QuotaExhausted);
        await using (var context = Db.CreateContext())
        {
            (await context.ProviderQuotas.SingleAsync()).Used.ShouldBe(2);
        }

        restarted.Fake.Advance(TimeSpan.FromSeconds(2)); // now 00:00:01 on the next UTC day

        (await provider.LookupWithStatusAsync(Code(3), default)).Status.ShouldBe(UpcLookupStatus.NotFound);
        await using (var context = Db.CreateContext())
        {
            (await context.ProviderQuotas.CountAsync()).ShouldBe(2); // one row per day
        }
    }

    [Fact]
    public async Task Does_not_search_by_title()
    {
        using var host = ProviderHost.Create(Db);

        (await host.Get<UpcItemDbProvider>().SearchAsync(new CatalogQuery { Title = "Super Mario 64" }, default)).ShouldBeEmpty();
        host.Server.LogEntries.ShouldBeEmpty();
    }
}
