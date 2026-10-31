using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// An auto-add of a read that names a format, where the release it matched is one a provider returned in this very request: that
// release is in the change tracker and not in the database until the request's single save. Finding the release the item is added
// as must not depend on the database having it.
public class AutoCommitProviderFormatTests : IdentifyTest
{
    public AutoCommitProviderFormatTests(ApiFixture api)
        : base(api)
    {
    }

    private static object OptIn() => new { autoCommitTiers = new[] { "T0", "T1" } };

    private static string? Tier(JsonElement candidate) =>
        candidate.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;

    private static Guid? CreatedItemId(JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;

    private static ProviderRelease Zorblax(ReleaseFormat? format) =>
        ScanData.Game("Zorblax Quest IV", "Nintendo 64", 1998, "g-zorblax") with { Format = format };

    private static ProviderRelease Film(ReleaseFormat? format) => new()
    {
        Provider = "igdb",
        ExternalId = "f-nightfall",
        Title = "Nightfall Express",
        Kind = WorkKind.Film,
        Format = format,
        Year = 1987,
    };

    private async Task<JsonElement> IdentifyShotAsync(HttpClient client, Guid sessionId, string shotKind, object item)
    {
        var body = Body(Guid.NewGuid(), [item]);
        body["shotKind"] = shotKind;
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, string.Join(" || ", Logs.Lines.Where(l => l.Contains("Exception", StringComparison.Ordinal))));
        return (await ReadJsonAsync(response)).GetProperty("candidates")[0];
    }

    private async Task<Release> ReleaseAsync(Guid releaseId)
    {
        await using var context = Db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == releaseId);
    }

    private async Task<List<Release>> ReleasesOfWorkAsync(Guid workId)
    {
        await using var context = Db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().AsNoTracking().Where(r => r.WorkId == workId).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task T1_adds_a_provider_release_whose_format_the_read_names()
    {
        var library = await CreateLibraryAsync();
        Games.Results.Add(Zorblax(ReleaseFormat.GameCartridge));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Zorblax Quest IV", kind: "game", format: "game_cartridge", platform: "Nintendo 64", year: 1998));

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T1");
        CreatedItemId(candidate).ShouldNotBeNull();
        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).Single();
        var release = await ReleaseAsync(item.ReleaseId);
        release.Source.ShouldBe(ReleaseSource.Provider, "the item is the provider's release itself; the read named the format it already has");
        release.OwnerLibraryId.ShouldBeNull();
        release.Format.ShouldBe(ReleaseFormat.GameCartridge);
        (await ReleasesOfWorkAsync(release.WorkId)).Count.ShouldBe(1, "no second release was made");
    }

    [Fact]
    public async Task T1_adds_a_provider_release_that_named_no_format_in_the_format_the_read_names()
    {
        var library = await CreateLibraryAsync();
        Games.Results.Add(Film(null));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Nightfall Express", kind: "film", format: "dvd", year: 1987));

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T1");
        CreatedItemId(candidate).ShouldNotBeNull();
        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).Single();
        var release = await ReleaseAsync(item.ReleaseId);
        release.Format.ShouldBe(ReleaseFormat.Dvd);
        (await ReleasesOfWorkAsync(release.WorkId)).Count.ShouldBe(1, "the read's format fills in the one release the provider gave");
    }

    [Fact]
    public async Task T1_adds_a_provider_release_in_the_format_it_names_when_the_read_names_another()
    {
        var library = await CreateLibraryAsync();
        Games.Results.Add(Film(ReleaseFormat.Bluray));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Nightfall Express", kind: "film", format: "dvd", year: 1987));

        Tier(candidate).ShouldBe("T1");
        CreatedItemId(candidate).ShouldNotBeNull();
        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).Single();
        (await ReleaseAsync(item.ReleaseId)).Format.ShouldBe(ReleaseFormat.Bluray, "a format the catalogue knows is never overridden by a read");
    }

    [Fact]
    public async Task A_background_job_adds_the_release_of_a_correction_made_while_the_read_was_pending()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var slow = true;
        Games.Results.Add(Zorblax(ReleaseFormat.GameCartridge));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var pending = (await IdentifyOkAsync(editor, sessionId, Read(title: "Zorblax Quest IV", kind: "game", year: 1998)))[0];
        Resolution(pending).ShouldBe("pending");
        (await DecideAsync(editor, sessionId, Id(pending), new { format = "game_disc", edition = "Collector's Box" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched", string.Join(" || ", Logs.Lines.Where(l => l.Contains("Exception", StringComparison.Ordinal))));
        CreatedItemId(resolved).ShouldNotBeNull("the correction names a release the job has to make beside the provider's");
        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).Single();
        var added = await ReleaseAsync(item.ReleaseId);
        added.Format.ShouldBe(ReleaseFormat.GameDisc);
        added.Edition.ShouldBe("Collector's Box");
        added.Source.ShouldBe(ReleaseSource.User);
        added.OwnerLibraryId.ShouldBe(library.Id);
        var siblings = await ReleasesOfWorkAsync(added.WorkId);
        siblings.Count.ShouldBe(2, "the provider's release is kept beside the library's own");
        siblings.ShouldContain(r => r.Source == ReleaseSource.Provider && r.Format == ReleaseFormat.GameCartridge);
    }

    [Fact]
    public async Task T1_adds_two_copies_of_a_film_read_in_one_picture_as_one_release_in_the_format_they_were_read()
    {
        var library = await CreateLibraryAsync();
        var stored = await CatalogAsync("Nightfall Express", ScanData.Upc(6411), ReleaseFormat.Unknown, WorkKind.Film, 1987);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var body = Body(Guid.NewGuid(), [DvdAt(0.10), DvdAt(0.60)]);
        body["shotKind"] = "shelf_segment";

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, string.Join(" || ", Logs.Lines.Where(l => l.Contains("Exception", StringComparison.Ordinal))));
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2, "two copies on two places of the shelf");
        items.Select(i => i.ReleaseId).Distinct().Count().ShouldBe(1, "both are the one DVD release, not one each");
        var release = await ReleaseAsync(items[0].ReleaseId);
        release.Format.ShouldBe(ReleaseFormat.Dvd);
        release.OwnerLibraryId.ShouldBe(library.Id);
        (await ReleasesOfWorkAsync((await ReleaseAsync(stored)).WorkId)).Count.ShouldBe(2, "the catalogue's release and one DVD of the library's own");
    }

    private static Dictionary<string, object?> DvdAt(double x)
    {
        var read = Read(title: "Nightfall Express", kind: "film", format: "dvd", year: 1987);
        read["box"] = new { x, y = 0.1, width = 0.05, height = 0.8 };
        return read;
    }

    [Fact]
    public async Task T0_adds_the_provider_release_a_barcode_found_for_a_read_that_names_a_format()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0" } });
        var row = await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Wave Racer Sixty Four", kind: "game", format: "game_cartridge", year: 1996));
        Resolution(row).ShouldBe("not_found");
        var code = ScanData.Upc(6401);
        Products.Know(code, ScanData.Product("Wave Racer Sixty Four", code));
        Games.Results.Add(ScanData.Game("Wave Racer Sixty Four"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/barcodes")
        {
            Content = Json(new { codes = new[] { Code(code) }, candidateId = Id(row) }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await editor.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, string.Join(" || ", Logs.Lines.Where(l => l.Contains("Exception", StringComparison.Ordinal))));
        var upgraded = (await ReadJsonAsync(response)).GetProperty("candidates")[0];
        Resolution(upgraded).ShouldBe("matched");
        Tier(upgraded).ShouldBe("T0");
        CreatedItemId(upgraded).ShouldNotBeNull();
        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).Single();
        (await ReleaseAsync(item.ReleaseId)).Format.ShouldBe(ReleaseFormat.GameCartridge);
    }
}
