using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.GoogleBooks;
using CollectionMaxxing.Infrastructure.Providers.OpenLibrary;
using CollectionMaxxing.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

public class OpenLibraryProviderTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly Identifier Isbn = ProviderCase.Isbn;

    private static void Stub(ProviderHost host, string path, string fixture) =>
        host.Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture(fixture)));

    [Fact]
    public async Task Looks_an_isbn_up_into_a_release()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "/api/books", "openlibrary-isbn-found.json");

        var release = await host.Get<OpenLibraryProvider>().LookupAsync(Isbn, default);

        release.ShouldNotBeNull();
        release.Provider.ShouldBe("openlibrary");
        release.ExternalId.ShouldBe("OL7353617M");
        release.Title.ShouldBe("Fantastic Mr. Fox: a play");
        release.Kind.ShouldBe(WorkKind.Book);
        release.Creators.ShouldBe(["Roald Dahl"]);
        release.Publisher.ShouldBe("Puffin");
        release.Year.ShouldBe(1988);
        release.CoverUrl.ShouldBe("https://covers.openlibrary.org/b/id/8739161-L.jpg");
        release.Identifiers.ShouldContain(Isbn);
    }

    [Fact]
    public async Task An_answer_that_starts_with_a_utf8_byte_order_mark_is_read_like_any_other()
    {
        using var host = ProviderHost.Create(Db);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        host.Server.Given(Request.Create().WithPath("/api/books").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody([.. bom, .. System.Text.Encoding.UTF8.GetBytes(ProviderHost.Fixture("openlibrary-isbn-found.json"))]));

        var release = await host.Get<OpenLibraryProvider>().LookupAsync(Isbn, default);

        release.ShouldNotBeNull();
        release.ExternalId.ShouldBe("OL7353617M");
    }

    [Fact]
    public async Task Asks_with_an_identifying_user_agent_that_carries_the_contact_email()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "/api/books", "openlibrary-isbn-found.json");

        await host.Get<OpenLibraryProvider>().LookupAsync(Isbn, default);

        var userAgent = host.Server.LogEntries.Single().RequestMessage!.Headers!["User-Agent"].ToString();
        userAgent.ShouldContain("CollectionMaxxing/1.0");
        userAgent.ShouldContain("owner@example.test");
    }

    [Fact]
    public async Task Searches_by_title_and_author()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "/search.json", "openlibrary-search.json");

        var results = await host.Get<OpenLibraryProvider>().SearchAsync(
            new CatalogQuery { Title = "Fantastic Mr Fox", Creator = "Dahl", Kind = WorkKind.Book }, default);

        results.Count.ShouldBe(2);
        results[0].ExternalId.ShouldBe("OL45883W");
        results[0].Creators.ShouldBe(["Roald Dahl"]);
        results[0].Year.ShouldBe(1970);
        results[0].CoverUrl.ShouldBe("https://covers.openlibrary.org/b/id/8739161-L.jpg");
        results[0].Identifiers.Select(i => i.Value).ShouldBe([ProviderHost.ValidIsbn]); // the junk isbn is ignored, 10 and 13 merge
        results[1].CoverUrl.ShouldBeNull();

        var query = host.Server.LogEntries.Single().RequestMessage!;
        query.Query!["title"].ToString().ShouldBe("Fantastic Mr Fox");
        query.Query["author"].ToString().ShouldBe("Dahl");
    }

    [Fact]
    public async Task Drops_a_cover_that_is_not_on_an_allow_listed_host()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "/api/books", "openlibrary-isbn-evil-cover.json");

        var release = await host.Get<OpenLibraryProvider>().LookupAsync(Isbn, default);

        release.ShouldNotBeNull();
        release.CoverUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Does_not_look_up_a_barcode_that_is_not_a_book()
    {
        using var host = ProviderHost.Create(Db);

        (await host.Get<OpenLibraryProvider>().LookupAsync(ProviderCase.Upc, default)).ShouldBeNull();
        host.Server.LogEntries.ShouldBeEmpty();
    }
}

public class GoogleBooksProviderTests(PostgresFixture db) : DatabaseTest(db)
{
    private static void Stub(ProviderHost host, string fixture) =>
        host.Server.Given(Request.Create().WithPath("/books/v1/volumes").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture(fixture)));

    [Fact]
    public async Task Looks_an_isbn_up_into_a_release_with_an_https_cover()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "googlebooks-found.json");

        var release = await host.Get<GoogleBooksProvider>().LookupAsync(ProviderCase.Isbn, default);

        release.ShouldNotBeNull();
        release.ExternalId.ShouldBe("Zk9bAwAAQBAJ");
        release.Title.ShouldBe("Fantastic Mr Fox");
        release.Creators.ShouldBe(["Roald Dahl"]);
        release.Publisher.ShouldBe("Penguin");
        release.Year.ShouldBe(2007);
        release.Synopsis.ShouldBe("A clever fox outwits three farmers.");
        release.CoverUrl.ShouldStartWith("https://books.google.com/books/content?id=Zk9bAwAAQBAJ");
        release.Identifiers.Select(i => i.Value).ShouldBe([ProviderHost.ValidIsbn]);
    }

    [Fact]
    public async Task Sends_the_key_in_a_header_and_never_in_the_url()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "googlebooks-found.json");

        await host.Get<GoogleBooksProvider>().LookupAsync(ProviderCase.Isbn, default);

        var request = host.Server.LogEntries.Single().RequestMessage!;
        request.Headers!["x-goog-api-key"].ToString().ShouldBe("google-test-key");
        request.Url.ShouldNotContain("google-test-key");
        request.Query!["q"].ToString().ShouldBe($"isbn:{ProviderHost.ValidIsbn}");
    }

    [Fact]
    public async Task Drops_a_cover_that_is_not_on_an_allow_listed_host()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "googlebooks-evil-cover.json");

        var results = await host.Get<GoogleBooksProvider>().SearchAsync(new CatalogQuery { Title = "Fantastic Mr Fox" }, default);

        results.Count.ShouldBe(1);
        results[0].CoverUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Is_switched_off_without_a_key_and_makes_no_calls()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:GoogleBooks:ApiKey"] = null });
        var provider = host.Get<GoogleBooksProvider>();

        provider.Supports(WorkKind.Book).ShouldBeFalse();
        (await provider.LookupAsync(ProviderCase.Isbn, default)).ShouldBeNull();
        (await provider.SearchAsync(new CatalogQuery { Title = "Fox" }, default)).ShouldBeEmpty();
        host.Server.LogEntries.ShouldBeEmpty();
    }
}
