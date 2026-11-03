using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CollectionMaxxing.Api.Tests.Catalog.ScriptedByTitle;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The eval measures what <c>/identify</c> does: a spine on a shelf of books whose read found films ("cnoi Emergency Contact o") is
/// asked again as a book by the one piece of code both run, so the same shelf gets the same verdicts and the same releases from
/// either. Real Postgres for the local catalogue (the trigram search, and the kinds of the releases the spines found), scripted
/// providers that know their own kind only.
/// </summary>
public class EvalGroundingShotKindTests(ApiFixture api) : EvalTest(api)
{
    /// <summary>A word no other test can have left in the shared catalogue: a release a snap ensured stays there, and the local search would find it next time.</summary>
    private string Tag { get; } = $"Zq{Guid.NewGuid():N}"[..14];

    private string Misread => $"cnoi Emergency Contact {Tag} o";

    private readonly ScriptedByKind _books = new(WorkKind.Book);
    private readonly ScriptedByKind _films = new(WorkKind.Film);

    private (string Title, string? Author)[] Shelf =>
    [
        ($"A COURT OF MIST AND FURY {Tag}", "Sarah J. Maas"),
        ($"A COURT OF WINGS AND RUIN {Tag}", "Sarah J. Maas"),
        ($"ASSASSINS APPRENTICE {Tag}", "Robin Hobb"),
        (Misread, null),
    ];

    private ProviderRelease Rel(string title, WorkKind kind, string id, int year, params string[] creators) =>
        Release($"{title} {Tag}", kind, $"{id}-{Tag}", year, creators);

    private void UseScriptedProviders() => ConfigureServices = services =>
    {
        services.RemoveAll<ICatalogProvider>();
        services.AddSingleton<ICatalogProvider>(_books);
        services.AddSingleton<ICatalogProvider>(_films);
    };

    private async Task<IReadOnlyList<GroundedItem>> GroundedByTheEvalAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EvalGrounder>().GroundAsync(
            ScanEngine.VisionPipeline,
            [.. Shelf.Select(s => new EvalReadItemInput(new IdentifyItemInput(null, null, s.Title, s.Title, s.Author, null, null, null, null, null, null, null), null))],
            new EvalGroundingMemo(), printKind: null, CancellationToken.None);
    }

    private async Task<List<JsonElement>> IdentifiedAsync(HttpClient client, Guid sessionId) =>
        await IdentifyOkAsync(client, sessionId, [.. Shelf.Select(s => (object)Read(title: s.Title, creator: s.Author))]);

    private static List<string> AlternativeFormats(JsonElement candidate) =>
        [.. candidate.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("format").GetString()!)];

    /// <summary>What each read offers, as "title (format)", in order: the eval's candidates.</summary>
    private static List<string[]> OfferedBy(IReadOnlyList<GroundedItem> eval) =>
        [.. eval.Select(i => i.Candidates.Select(c => $"{c.Title} ({c.Format})").ToArray())];

    /// <summary>What each read offers, as "title (format)", in order: /identify's alternatives.</summary>
    private static List<string[]> OfferedBy(List<JsonElement> live) =>
        [.. live.Select(l => AlternativeTitles(l).Zip(AlternativeFormats(l), (title, format) => $"{title} ({format})").ToArray())];

    private void ArrangeKindedProviders()
    {
        _books
            .Know($"A COURT OF MIST AND FURY {Tag}", false, Rel("A Court of Mist and Fury", WorkKind.Book, "m1", 2016, "Sarah J. Maas"))
            .Know($"A COURT OF WINGS AND RUIN {Tag}", false, Rel("A Court of Wings and Ruin", WorkKind.Book, "m2", 2017, "Sarah J. Maas"))
            .Know($"ASSASSINS APPRENTICE {Tag}", false, Rel("Assassins Apprentice", WorkKind.Book, "m3", 1995, "Robin Hobb"))
            .Know(Misread, true, Rel("Emergency Contact", WorkKind.Book, "b1", 2018));
    }

    [Fact]
    public async Task The_eval_and_identify_give_a_shelf_of_provider_found_books_the_same_offer_of_the_book()
    {
        ArrangeKindedProviders();
        _films.Know(
            Misread, false, Rel("Emergency Contact", WorkKind.Film, "f1", 2019), Rel("Emergency Contact", WorkKind.Film, "f2", 2020), Rel("Emergency Contact", WorkKind.Film, "f3", 2021));
        UseScriptedProviders();
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var eval = await GroundedByTheEvalAsync(); // a dry run: it leaves nothing in the catalogue for the live request to find
        var live = await IdentifiedAsync(client, sessionId);

        eval.Select(i => i.Verdict).ShouldBe(live.Select(Resolution));
        OfferedBy(eval).ShouldBe(OfferedBy(live));
        Resolution(live[3]).ShouldBe("ambiguous");
        eval[3].Candidates.Select(c => c.Format).ShouldBe(["paperback", "dvd", "dvd"]); // and it was the ask as a book that found the book
    }

    [Fact]
    public async Task The_eval_and_identify_give_a_shelf_the_catalogue_holds_the_same_offer_of_the_book()
    {
        // The matched spines are the catalogue's own books, and the films the read found are the catalogue's too: their kinds are
        // only ids until they are looked up. The book found as a book comes from a provider.
        await CatalogSeed.AddAsync(Db, $"A Court of Mist and Fury {Tag}", WorkKind.Book, 2016, format: ReleaseFormat.Paperback, creators: ["Sarah J. Maas"]);
        await CatalogSeed.AddAsync(Db, $"A Court of Wings and Ruin {Tag}", WorkKind.Book, 2017, format: ReleaseFormat.Paperback, creators: ["Sarah J. Maas"]);
        await CatalogSeed.AddAsync(Db, $"Assassins Apprentice {Tag}", WorkKind.Book, 1995, format: ReleaseFormat.Paperback, creators: ["Robin Hobb"]);
        await CatalogSeed.AddAsync(Db, $"Emergency Contact {Tag}", WorkKind.Film, 2019, format: ReleaseFormat.Dvd);
        await CatalogSeed.AddAsync(Db, $"Emergency Contact {Tag}", WorkKind.Film, 2020, format: ReleaseFormat.Dvd);
        await CatalogSeed.AddAsync(Db, $"Emergency Contact {Tag}", WorkKind.Film, 2021, format: ReleaseFormat.Dvd);
        _books.Know(Misread, true, Rel("Emergency Contact", WorkKind.Book, "b1", 2018));
        UseScriptedProviders();
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var eval = await GroundedByTheEvalAsync();
        var live = await IdentifiedAsync(client, sessionId);

        eval.Select(i => i.Verdict).ShouldBe(live.Select(Resolution));
        OfferedBy(eval).ShouldBe(OfferedBy(live));
        eval.Take(3).Select(i => i.Verdict).ShouldAllBe(v => v == "matched");
        eval[3].Candidates.Select(c => c.Format).ShouldBe(["paperback", "dvd", "dvd"]);
        AlternativeFormats(live[3]).ShouldBe(["paperback", "dvd", "dvd"]);
    }

    [Fact]
    public async Task The_eval_and_identify_leave_a_shelf_as_it_was_when_the_pass_runs_out_of_time()
    {
        ArrangeKindedProviders();
        _films.Know(
            Misread, false, Rel("Emergency Contact", WorkKind.Film, "f1", 2019), Rel("Emergency Contact", WorkKind.Film, "f2", 2020), Rel("Emergency Contact", WorkKind.Film, "f3", 2021));
        _books.Hangs = (title, kind) => title == Misread && kind == WorkKind.Book; // the pass has the same fixed budget in both, and then stops
        UseScriptedProviders();
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var eval = await GroundedByTheEvalAsync();
        var live = await IdentifiedAsync(client, sessionId);

        eval.Select(i => i.Verdict).ShouldBe(live.Select(Resolution));
        OfferedBy(eval).ShouldBe(OfferedBy(live));
        AlternativeFormats(live[3]).ShouldBe(["dvd", "dvd", "dvd"]);
    }
}
