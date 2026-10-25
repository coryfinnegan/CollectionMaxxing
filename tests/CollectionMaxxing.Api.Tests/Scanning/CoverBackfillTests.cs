using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Books added before covers were fetched (or while fetching silently failed) gain their pictures from the nightly backfill.
public class CoverBackfillTests : ScanTest
{
    private const string CoverUrl = "https://covers.openlibrary.org/b/id/42-L.jpg";

    private readonly FakeCoverFetcher _covers = new();

    private int _providerCalls;

    public CoverBackfillTests(ApiFixture api) : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICoverFetcher>();
            services.AddSingleton<ICoverFetcher>(_covers);
        };
        Books.Behaviour = _ =>
        {
            _providerCalls++;
            return Task.CompletedTask;
        };
        Games.Behaviour = _ =>
        {
            _providerCalls++;
            return Task.CompletedTask;
        };
    }

    private async Task BackfillAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BackfillCovers>()
            .HandleAsync(new BackfillCovers.Job(), TestContext.Current.CancellationToken);
    }

    private static string Payload(string? coverUrl, string? status = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { provider = "openlibrary", externalId = "OL1M", coverUrl, coverStatus = status });

    private async Task<Guid> SeedReleaseAsync(
        string title, WorkKind kind, string? code, string? payloadJson, DateTimeOffset? refreshedAt = null)
    {
        Identifier? id = null;
        if (code is not null)
        {
            Identifier.TryParse(code, out id).ShouldBeTrue();
        }

        await using var context = Db.CreateContext();
        var work = new Work
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            NormalizedTitle = title.ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = work.Id,
            Format = kind == WorkKind.Book ? ReleaseFormat.Paperback : ReleaseFormat.GameCartridge,
            Source = ReleaseSource.Provider,
            ProviderPayloadsJson = payloadJson,
            RefreshedAt = refreshedAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Works.Add(work);
        context.Releases.Add(release);
        if (id is not null)
        {
            context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = release.Id, Type = id.Type, Value = id.Value });
        }

        await context.SaveChangesAsync();
        return release.Id;
    }

    private async Task<Release> ReleaseAsync(Guid releaseId)
    {
        await using var context = Db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == releaseId);
    }

    private static string? CoverStatusOf(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return null;
        }

        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("coverStatus", out var status)
            && status.ValueKind == System.Text.Json.JsonValueKind.String
            ? status.GetString()
            : null;
    }

    private static ProviderRelease Book(string isbn, string? coverUrl) => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL1M",
        Title = "Dune",
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        Year = 1965,
        CoverUrl = coverUrl,
        Identifiers = [Identifier.TryParse(isbn, out var id) ? id! : throw new InvalidOperationException()],
    };

    [Fact]
    public async Task The_backfill_gives_a_pre_existing_coverless_release_its_cover()
    {
        var releaseId = await SeedReleaseAsync("Dune", WorkKind.Book, ScanData.Isbn, Payload(CoverUrl));

        await BackfillAsync();

        _covers.Asked.ShouldBe([CoverUrl]);
        var release = await ReleaseAsync(releaseId);
        release.CoverAssetId.ShouldNotBeNull();
        CoverStatusOf(release.ProviderPayloadsJson).ShouldBe("fetched");
        await using var context = Db.CreateContext();
        var asset = await context.Assets.SingleAsync(a => a.Id == release.CoverAssetId);
        asset.Kind.ShouldBe(AssetKind.Cover);
        asset.SourceUrl.ShouldBe(CoverUrl);
    }

    [Fact]
    public async Task The_backfill_reresolves_a_release_whose_payload_names_no_cover()
    {
        var releaseId = await SeedReleaseAsync("Dune", WorkKind.Book, ScanData.Isbn, Payload(null));
        Books.LookupResult = Book(ScanData.Isbn, CoverUrl);

        await BackfillAsync();

        var release = await ReleaseAsync(releaseId);
        release.CoverAssetId.ShouldNotBeNull();
        CoverStatusOf(release.ProviderPayloadsJson).ShouldBe("fetched");
    }

    [Fact]
    public async Task A_release_whose_book_has_no_cover_anywhere_is_marked_and_left_alone_for_a_while()
    {
        var releaseId = await SeedReleaseAsync("Dune", WorkKind.Book, ScanData.Isbn, Payload(null));
        Books.LookupResult = Book(ScanData.Isbn, null);

        await BackfillAsync();

        var release = await ReleaseAsync(releaseId);
        release.CoverAssetId.ShouldBeNull();
        CoverStatusOf(release.ProviderPayloadsJson).ShouldBe("no-cover");
        release.RefreshedAt.ShouldNotBeNull();
        _covers.Asked.ShouldBeEmpty();
        var callsAfterFirst = _providerCalls;

        await BackfillAsync();

        _providerCalls.ShouldBe(callsAfterFirst, "a fresh no-cover answer is not asked again");
        _covers.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stale_no_cover_answer_is_asked_again()
    {
        var releaseId = await SeedReleaseAsync(
            "Dune", WorkKind.Book, ScanData.Isbn, Payload(null, "no-cover"), DateTimeOffset.UtcNow.AddDays(-31));
        Books.LookupResult = Book(ScanData.Isbn, CoverUrl);

        await BackfillAsync();

        _providerCalls.ShouldBeGreaterThan(0);
        (await ReleaseAsync(releaseId)).CoverAssetId.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_backfill_never_spends_the_product_lookup_quota()
    {
        var releaseId = await SeedReleaseAsync("A game", WorkKind.Game, ScanData.Upc(9101), Payload(null));

        await BackfillAsync();

        Products.Calls.ShouldBe(0);
        _providerCalls.ShouldBe(0);
        _covers.Asked.ShouldBeEmpty();
        var release = await ReleaseAsync(releaseId);
        release.CoverAssetId.ShouldBeNull();
        CoverStatusOf(release.ProviderPayloadsJson).ShouldBe("no-cover");
    }

    [Fact]
    public async Task A_failed_backfill_fetch_is_recorded_and_retried_next_time()
    {
        var releaseId = await SeedReleaseAsync("Dune", WorkKind.Book, ScanData.Isbn, Payload(CoverUrl));
        _covers.Fails = true;

        await BackfillAsync();

        var failed = await ReleaseAsync(releaseId);
        failed.CoverAssetId.ShouldBeNull();
        CoverStatusOf(failed.ProviderPayloadsJson).ShouldBe("failed");

        _covers.Fails = false;
        await BackfillAsync();

        var fetched = await ReleaseAsync(releaseId);
        fetched.CoverAssetId.ShouldNotBeNull();
        CoverStatusOf(fetched.ProviderPayloadsJson).ShouldBe("fetched");
        await using var context = Db.CreateContext();
        (await context.Assets.IgnoreQueryFilters().CountAsync(a => a.Kind == AssetKind.Cover)).ShouldBe(1);
    }

    [Fact]
    public async Task The_backfill_forgets_stale_kept_answers_before_reasking()
    {
        var releaseId = await SeedReleaseAsync("Dune", WorkKind.Book, ScanData.Isbn, Payload(null));
        Books.LookupResult = Book(ScanData.Isbn, null);
        await BackfillAsync();
        CoverStatusOf((await ReleaseAsync(releaseId)).ProviderPayloadsJson).ShouldBe("no-cover");

        // The providers learn the cover a month later; the kept coverless answer must not win.
        await using (var context = Db.CreateContext())
        {
            var release = await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == releaseId);
            release.RefreshedAt = DateTimeOffset.UtcNow.AddDays(-31);
            await context.SaveChangesAsync();
        }

        Books.LookupResult = Book(ScanData.Isbn, CoverUrl);
        await BackfillAsync();

        var healed = await ReleaseAsync(releaseId);
        healed.CoverAssetId.ShouldNotBeNull();
        CoverStatusOf(healed.ProviderPayloadsJson).ShouldBe("fetched");
    }

    [Fact]
    public void The_backfill_runs_once_a_day()
    {
        var recurring = Factory.Services.GetServices<RecurringJobRegistration>().ToList();

        recurring.ShouldContain(r => r.Type == BackfillCovers.JobType && r.Every == TimeSpan.FromDays(1));
    }
}
