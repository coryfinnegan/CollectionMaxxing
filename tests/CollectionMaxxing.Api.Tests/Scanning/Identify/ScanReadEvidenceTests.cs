using System.Net;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// Shapes from real scans on the owner's server (2026-10-24 to 10-29). A movie cover lost its format on the way to the
// library (TMDB names none, so the release was stored Unknown), and a book cover whose read carried the model's own enum
// words (platform "manualVisible", a loose-cart completeness, a bestseller line as the edition) was scored as a cartridge
// and stuck around 0.75 although its title and author were exact.
public class ScanReadEvidenceTests(ApiFixture api) : IdentifyTest(api)
{
    private async Task<(Guid LibraryId, HttpClient Client, Guid SessionId)> StartAsync()
    {
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        return (library.Id, client, await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

    private async Task<ReleaseFormat> CommittedFormatAsync(Guid libraryId)
    {
        await using var context = Db.CreateContext();
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == libraryId);
        return (await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == item.ReleaseId)).Format;
    }

    [Theory]
    [InlineData("dvd", ReleaseFormat.Dvd)]
    [InlineData("vhs", ReleaseFormat.Vhs)]
    [InlineData("bluray", ReleaseFormat.Bluray)]
    public async Task A_movie_cover_is_matched_and_added_in_the_format_it_was_read_as(string format, ReleaseFormat expected)
    {
        var (libraryId, client, sessionId) = await StartAsync();
        Games.Results.Add(new ProviderRelease
        {
            Provider = "tmdb",
            ExternalId = "movie/603",
            WorkKey = "movie/603",
            Title = "The Matrix",
            Kind = WorkKind.Film,
            Year = 1999,
        });

        var candidate = (await IdentifyOkAsync(client, sessionId, Read(title: "The Matrix", kind: "film", format: format))).Single();

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("release").GetProperty("format").GetString().ShouldBe(format);
        var commit = await CommitAsync(client, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await CommittedFormatAsync(libraryId)).ShouldBe(expected);
    }

    [Fact]
    public async Task A_movie_the_catalogue_keeps_with_an_unknown_format_is_added_in_the_format_it_was_read_as()
    {
        var (libraryId, client, sessionId) = await StartAsync();
        var (workId, _) = await CatalogSeed.AddAsync(Db, "Jaws", WorkKind.Film, 1975, format: ReleaseFormat.Unknown);

        var candidate = (await IdentifyOkAsync(client, sessionId, Read(title: "Jaws", kind: "film", format: "vhs"))).Single();

        Resolution(candidate).ShouldBe("matched");
        var commit = await CommitAsync(client, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());

        (await CommittedFormatAsync(libraryId)).ShouldBe(ReleaseFormat.Vhs);
        await using var context = Db.CreateContext();
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == libraryId);
        (await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == item.ReleaseId)).WorkId.ShouldBe(workId);
    }

    [Fact]
    public async Task A_book_cover_read_with_the_models_junk_fields_is_matched_on_its_title_and_author()
    {
        var (_, client, sessionId) = await StartAsync();
        Books.Results.Add(new ProviderRelease
        {
            Provider = "openlibrary",
            ExternalId = "OL999W",
            WorkKey = "OL999W",
            Title = "Suddenly You",
            Kind = WorkKind.Book,
            Year = 2001,
            Creators = ["Lisa Kleypas"],
        });

        var candidate = (await IdentifyOkAsync(
            client,
            sessionId,
            Read(
                title: "Suddenly You",
                creator: "LISA KLEYPAS",
                kind: "book",
                platform: "manualVisible",
                completeness: "cart_only",
                edition: "NEW YORK TIMES BESTSELLING AUTHOR"))).Single();

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("score").GetDouble().ShouldBe(1.0);

        await using var context = Db.CreateContext();
        var stored = (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(candidate))).InferredJson;
        stored.ShouldNotBeNull();
        stored.ShouldContain("\"profile\": \"Cover\"", Case.Insensitive);
        stored.ShouldNotContain("manualVisible");
        stored.ShouldNotContain("cart_only");
        stored.ShouldNotContain("BESTSELLING");
    }
}
