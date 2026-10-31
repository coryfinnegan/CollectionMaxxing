using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// On a shelf shot the picture model adds the items Vision found nothing at, and it sometimes invents a title that happens to exist
// ("food for comfort" read for "Modern Comfort Food"). The phone marks such a read unconfirmed. The catalogue can still match it
// exactly, but a read nothing else saw is never committed by itself: it is a T2 row, reviewed by the person. Only the commit tier
// moves, and the default decision: a title nobody else saw is not added by default, the person opts in. The candidates, their order
// and the verdict are what the same read would have got without the mark.
public class UnconfirmedReadTests(ApiFixture api) : IdentifyTest(api)
{
    private static object OptIn() => new { autoCommitTiers = new[] { "T0", "T1" } };

    private static string? Tier(JsonElement candidate) =>
        candidate.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;

    private static Guid? CreatedItemId(JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;

    private static Dictionary<string, object?> StarFox(bool? unconfirmed)
    {
        var read = Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996);
        if (unconfirmed is { } flag)
        {
            read["unconfirmed"] = flag;
        }

        return read;
    }

    private static async Task<List<JsonElement>> IdentifyShelfAsync(HttpClient client, Guid sessionId, params object[] items)
    {
        var body = Body(Guid.NewGuid(), items);
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    [Fact]
    public async Task An_unconfirmed_read_the_catalogue_matches_exactly_is_T2_and_is_not_auto_added()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(91101));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = (await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: true)))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T2");
        Decision(candidate).ShouldBe("undecided", "an unconfirmed read is not pre-accepted; the person opts in");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("counters").GetProperty("added").GetInt32().ShouldBe(0);
        session.GetProperty("counters").GetProperty("toReview").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task An_unconfirmed_read_gets_the_same_candidates_and_verdict_as_the_same_read_without_the_mark()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(91102));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);

        var confirmed = (await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: null)))[0];
        var unconfirmed = (await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: true)))[0];

        Tier(confirmed).ShouldBe("T1");
        Tier(unconfirmed).ShouldBe("T2");
        Resolution(unconfirmed).ShouldBe(Resolution(confirmed));
        Decision(confirmed).ShouldBe("accepted");
        Decision(unconfirmed).ShouldBe("undecided", "the default decision is the only other thing the mark moves");
        unconfirmed.GetProperty("score").GetDouble().ShouldBe(confirmed.GetProperty("score").GetDouble());
        unconfirmed.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(confirmed.GetProperty("release").GetProperty("id").GetGuid());
        unconfirmed.GetProperty("alternatives").GetRawText().ShouldBe(confirmed.GetProperty("alternatives").GetRawText());
    }

    [Fact]
    public async Task An_unconfirmed_read_the_person_never_accepted_is_not_added_at_commit()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(91107));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: true));

        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unconfirmed_read_the_person_accepts_is_added_at_commit()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(91109));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var candidate = (await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: true)))[0];

        var decided = await DecideAsync(editor, sessionId, Id(candidate), new { decision = "accepted" });
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, await decided.Content.ReadAsStringAsync());
        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unconfirmed_read_of_a_release_the_library_owns_is_skipped_as_any_owned_hit_is()
    {
        var library = await CreateLibraryAsync();
        await OwnAsync(library, "Star Fox 64", ScanData.Upc(91108));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = (await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: true)))[0];

        Tier(candidate).ShouldBe("T2");
        Decision(candidate).ShouldBe("rejected");
        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_confirmed_read_stays_T1_and_is_auto_added_whether_the_mark_is_false_or_missing()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(91103));
        await CatalogAsync("Wave Race 64", ScanData.Upc(91104));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var waveRace = Read(title: "Wave Race 64", kind: "game", format: "game_cartridge", year: 1996);

        var candidates = await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: false), waveRace);

        candidates.ShouldAllBe(c => Tier(c) == "T1");
        candidates.ShouldAllBe(c => CreatedItemId(c) != null);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Only_the_unconfirmed_read_of_a_shelf_waits_for_review_the_others_still_auto_add()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(91105));
        await CatalogAsync("Wave Race 64", ScanData.Upc(91106));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var waveRace = Read(title: "Wave Race 64", kind: "game", format: "game_cartridge", year: 1996);
        waveRace["unconfirmed"] = true;

        var candidates = await IdentifyShelfAsync(editor, sessionId, StarFox(unconfirmed: null), waveRace);

        Tier(candidates[0]).ShouldBe("T1");
        CreatedItemId(candidates[0]).ShouldNotBeNull();
        Tier(candidates[1]).ShouldBe("T2");
        CreatedItemId(candidates[1]).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_mark_is_kept_with_the_read_so_the_background_job_gives_the_same_tier()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var slow = true;
        Games.Results.Add(ScanData.Game("Zorblax Quest IV", "Nintendo 64", 1998, "g-zorblax"));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var read = Read(title: "Zorblax Quest IV", kind: "game");
        read["unconfirmed"] = true;
        Resolution((await IdentifyShelfAsync(editor, sessionId, read))[0]).ShouldBe("pending");

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched");
        Tier(resolved).ShouldBe("T2");
        Decision(resolved).ShouldBe("undecided");
        CreatedItemId(resolved).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_background_job_still_auto_adds_a_confirmed_T1_read()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var slow = true;
        Games.Results.Add(ScanData.Game("Zorblax Quest V", "Nintendo 64", 1998, "g-zorblax-5"));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        Resolution((await IdentifyShelfAsync(editor, sessionId, Read(title: "Zorblax Quest V", kind: "game")))[0]).ShouldBe("pending");

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Tier(resolved).ShouldBe("T1");
        CreatedItemId(resolved).ShouldNotBeNull();
    }
}
