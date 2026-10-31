using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// The provider hits a search showed, kept for a day so an add can name one by provider and id (Postgres and a fake clock).
/// </summary>
public class ProviderHitStoreTests(PostgresFixture db) : DatabaseTest(db)
{
    private static ProviderRelease Hit(string externalId = "OL1M", string title = "Suddenly You", string? coverUrl = null) => new()
    {
        Provider = "openlibrary",
        ExternalId = externalId,
        WorkKey = "OL1W",
        Title = title,
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        Year = 2001,
        Creators = ["Lisa Kleypas"],
        Edition = "Mass market",
        Publisher = "Avon",
        CoverUrl = coverUrl,
        Synopsis = "A governess and a duke.",
        Identifiers = [Identifier.TryParse("9780380817771", out var isbn) ? isbn : throw new InvalidOperationException("test ISBN")],
    };

    [Fact]
    public async Task A_saved_hit_comes_back_whole()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();

        await store.SaveAsync([Hit(coverUrl: "https://covers.openlibrary.org/b/id/1-L.jpg")], default);

        var found = await store.FindAsync("openlibrary", "OL1M", default);
        found.ShouldNotBeNull();
        found.Provider.ShouldBe("openlibrary");
        found.ExternalId.ShouldBe("OL1M");
        found.WorkKey.ShouldBe("OL1W");
        found.Title.ShouldBe("Suddenly You");
        found.Kind.ShouldBe(WorkKind.Book);
        found.Format.ShouldBe(ReleaseFormat.Paperback);
        found.Year.ShouldBe(2001);
        found.Creators.ShouldBe(["Lisa Kleypas"]);
        found.Edition.ShouldBe("Mass market");
        found.Publisher.ShouldBe("Avon");
        found.CoverUrl.ShouldBe("https://covers.openlibrary.org/b/id/1-L.jpg");
        found.Synopsis.ShouldBe("A governess and a duke.");
        found.Identifiers.Select(i => i.Value).ShouldBe(["9780380817771"]);
    }

    [Fact]
    public async Task A_hit_nobody_saved_is_not_found()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();
        await store.SaveAsync([Hit()], default);

        (await store.FindAsync("openlibrary", "OL2M", default)).ShouldBeNull();
        (await store.FindAsync("googlebooks", "OL1M", default)).ShouldBeNull();
    }

    [Fact]
    public async Task The_same_id_from_two_providers_is_two_hits()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();
        await store.SaveAsync([Hit("42", "From Open Library"), Hit("42", "From Google Books") with { Provider = "googlebooks" }], default);

        (await store.FindAsync("openlibrary", "42", default))!.Title.ShouldBe("From Open Library");
        (await store.FindAsync("googlebooks", "42", default))!.Title.ShouldBe("From Google Books");
    }

    [Fact]
    public async Task Saving_a_hit_again_replaces_the_earlier_copy_and_restarts_its_day()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();
        await store.SaveAsync([Hit(title: "Old title")], default);
        host.Fake.Advance(TimeSpan.FromHours(20));

        await store.SaveAsync([Hit(title: "New title")], default);
        host.Fake.Advance(TimeSpan.FromHours(20));

        (await store.FindAsync("openlibrary", "OL1M", default))!.Title.ShouldBe("New title");
    }

    [Fact]
    public async Task A_hit_is_gone_after_its_day()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();
        await store.SaveAsync([Hit()], default);

        host.Fake.Advance(TimeSpan.FromHours(23));
        (await store.FindAsync("openlibrary", "OL1M", default)).ShouldNotBeNull();

        host.Fake.Advance(TimeSpan.FromHours(2));
        (await store.FindAsync("openlibrary", "OL1M", default)).ShouldBeNull();
    }

    [Fact]
    public async Task Saving_clears_the_hits_that_have_expired_and_nothing_else()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();
        await store.SaveAsync([Hit("old")], default);
        await using (var context = Db.CreateContext())
        {
            context.ProviderCache.Add(new ProviderCacheEntry
            {
                Provider = "openlibrary",
                Key = "lookup:isbn:1",
                PayloadJson = "{}",
                FetchedAt = host.Time.GetUtcNow().AddDays(-40),
                ExpiresAt = host.Time.GetUtcNow().AddDays(-10),
            });
            await context.SaveChangesAsync();
        }

        host.Fake.Advance(TimeSpan.FromDays(2));
        await store.SaveAsync([Hit("fresh")], default);

        await using var check = Db.CreateContext();
        var keys = await check.ProviderCache.AsNoTracking().Select(c => c.Provider + "/" + c.Key).OrderBy(k => k).ToListAsync();
        keys.Count.ShouldBe(2);
        keys.ShouldContain("openlibrary/lookup:isbn:1"); // another cache's expired row is that cache's business
        keys.Count(k => k.Contains("fresh", StringComparison.Ordinal)).ShouldBe(1);
        keys.ShouldNotContain(k => k.Contains("old", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_searches_saving_the_same_hits_at_once_both_succeed()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();
        var hits = Enumerable.Range(0, 20).Select(i => Hit($"OL{i}M")).ToList();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.SaveAsync(hits, default)));

        (await store.FindAsync("openlibrary", "OL19M", default)).ShouldNotBeNull();
        await using var check = Db.CreateContext();
        (await check.ProviderCache.AsNoTracking().CountAsync()).ShouldBe(20);
    }

    [Fact]
    public async Task A_hit_with_an_unusable_key_is_skipped_and_the_rest_are_kept()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var store = host.Get<IProviderHitStore>();

        await store.SaveAsync([Hit(new string('x', 600)), Hit("fine")], default);

        (await store.FindAsync("openlibrary", "fine", default)).ShouldNotBeNull();
        (await store.FindAsync("openlibrary", new string('x', 600), default)).ShouldBeNull();
    }
}
