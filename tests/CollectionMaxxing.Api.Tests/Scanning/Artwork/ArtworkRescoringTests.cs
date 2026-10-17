using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Tests.Scanning.Artwork;

public class ArtworkRescoringTests(ApiFixture api) : ArtworkTest(api)
{
    [Fact]
    public async Task A_near_miss_pair_that_text_alone_cannot_separate_is_matched_when_the_right_releases_artwork_is_close_and_the_others_is_far()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        Resolution(scan.Candidate).ShouldBe("ambiguous"); // text alone: two equal scores, no lead
        var ids = AlternativeIds(scan.Candidate);
        var right = ids[1]; // not the one text happened to list first

        var candidate = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(right, 0.1)));

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(right);
        AlternativeIds(candidate).ShouldBe([right, ids[0]]);
        candidate.GetProperty("score").GetDouble().ShouldBeGreaterThanOrEqualTo(0.90);
        candidate.GetProperty("artDistance").GetDouble().ShouldBe(0.1);
        candidate.GetProperty("needsArt").GetBoolean().ShouldBeFalse();
        Decision(candidate).ShouldBe("accepted");
    }

    [Fact]
    public async Task Artwork_that_cannot_tell_the_two_apart_leaves_the_item_ambiguous()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);

        var candidate = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.1), Distance(ids[1], 0.1)));

        Resolution(candidate).ShouldBe("ambiguous");
        Decision(candidate).ShouldBe("undecided");
        AlternativeIds(candidate).ToHashSet().ShouldBe(ids.ToHashSet());
    }

    [Fact]
    public async Task Artwork_that_matches_neither_keeps_both_for_the_person_to_choose_and_never_says_not_found()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);

        var candidate = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.4), Distance(ids[1], 1.4)));

        Resolution(candidate).ShouldBe("ambiguous");
        AlternativeIds(candidate).ToHashSet().ShouldBe(ids.ToHashSet());
        Decision(candidate).ShouldBe("undecided");
    }

    [Fact]
    public async Task A_distance_for_one_release_leaves_the_other_neutral_so_it_cannot_win_by_having_no_art_term()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);

        // Only the first is compared, and it is a poor match: the other must not profit from the missing number.
        var candidate = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.4)));

        Resolution(candidate).ShouldBe("ambiguous");
        AlternativeIds(candidate)[0].ShouldBe(ids[1]); // neutral (0.5) beats a bad match
        Alternatives(candidate)[0].GetProperty("score").GetDouble().ShouldBeGreaterThan(Alternatives(candidate)[1].GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task Without_any_distances_the_art_weight_is_redistributed_so_the_text_score_stands()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();

        // Every other term agrees, so the score is 1: the art term was left out, not counted as zero.
        scan.Candidate.GetProperty("score").GetDouble().ShouldBe(1.0, 1e-6);
        scan.Candidate.GetProperty("artDistance").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Posting_again_replaces_the_earlier_distances_instead_of_adding_to_them()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var first = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.1), Distance(ids[1], 1.3)));
        Resolution(first).ShouldBe("matched");
        first.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(ids[0]);

        var second = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1)));

        Resolution(second).ShouldBe("matched");
        second.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(ids[1]);
        second.GetProperty("artDistance").GetDouble().ShouldBe(0.1);

        // The very same request twice changes the answer's content, not its meaning.
        var again = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1)));
        again.GetProperty("score").GetDouble().ShouldBe(second.GetProperty("score").GetDouble());
        again.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(ids[1]);
        again.GetProperty("changeSeq").GetInt32().ShouldBeGreaterThan(second.GetProperty("changeSeq").GetInt32());
    }

    [Fact]
    public async Task The_updated_candidate_gets_a_higher_change_seq_and_the_since_fallback_returns_it()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var changeSeqBefore = scan.Candidate.GetProperty("changeSeq").GetInt32();
        var other = (await IdentifyOkAsync(scan.Client, scan.SessionId, Read(title: "Zorblax Quest IV", kind: "game")))[0];
        var polledUpTo = other.GetProperty("changeSeq").GetInt32();
        polledUpTo.ShouldBeGreaterThan(changeSeqBefore);

        var updated = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.1), Distance(ids[1], 1.3)));

        updated.GetProperty("changeSeq").GetInt32().ShouldBeGreaterThan(polledUpTo);
        updated.GetProperty("seq").GetInt32().ShouldBe(scan.Candidate.GetProperty("seq").GetInt32()); // seq is creation order and stays put
        var since = (await GetSessionAsync(scan.Client, scan.SessionId, since: polledUpTo)).GetProperty("candidates").EnumerateArray().ToList();
        since.Count.ShouldBe(1);
        Id(since[0]).ShouldBe(scan.CandidateId);
        Resolution(since[0]).ShouldBe("matched");
        (await CountCandidatesAsync(scan.SessionId)).ShouldBe(2); // the update added nothing
    }

    [Fact]
    public async Task A_matched_item_whose_lead_is_small_is_a_close_call_the_server_can_still_correct()
    {
        // The read says "Player's Choice"; one release is exactly that, the other a different re-release that trails it by only a little.
        var scan = await ScanCloseCallAsync(
            "Pokemon Stadium",
            async platformId =>
                [await ReleaseAsync("Pokemon Stadium", platformId, edition: "Player's Choice"), await ReleaseAsync("Pokemon Stadium", platformId, edition: "Greatest Hits")],
            edition: "Player's Choice");
        Resolution(scan.Candidate).ShouldBe("matched");
        Decision(scan.Candidate).ShouldBe("accepted"); // included by default
        scan.Candidate.GetProperty("needsArt").GetBoolean().ShouldBeTrue();
        var ids = AlternativeIds(scan.Candidate);

        // The phone finds neither box convincing, the second a little less bad: the server takes it back to "pick one".
        var candidate = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.9)));

        Resolution(candidate).ShouldBe("ambiguous");
        Decision(candidate).ShouldBe("undecided");
        candidate.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        (await CommitAsync(scan.Client, scan.SessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountItemsAsync(scan.Library.Id)).ShouldBe(0); // nothing was added behind the person's back
    }

    [Fact]
    public async Task A_person_who_accepts_the_servers_own_pick_makes_it_their_decision_and_art_no_longer_changes_it()
    {
        // The server auto-accepts the top pick of a close call (the first test keeps the flip), then the person accepts that same pick.
        var scan = await ScanCloseMatchAsync();
        Decision(scan.Candidate).ShouldBe("accepted");
        var ids = AlternativeIds(scan.Candidate);
        (await DecideAsync(scan.Client, scan.SessionId, scan.CandidateId, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.9));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
        Decision(await CandidateNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe("accepted");
    }

    private Task<CloseCall> ScanCloseMatchAsync() => ScanCloseCallAsync(
        "Pokemon Stadium",
        async platformId =>
            [await ReleaseAsync("Pokemon Stadium", platformId, edition: "Player's Choice"), await ReleaseAsync("Pokemon Stadium", platformId, edition: "Greatest Hits")],
        edition: "Player's Choice");

    [Fact]
    public async Task A_matched_item_with_a_clear_lead_is_not_a_close_call_and_has_nothing_to_compare()
    {
        var scan = await ScanCloseCallAsync(
            "Pokemon Stadium",
            async platformId => [await ReleaseAsync("Pokemon Stadium", platformId), await ReleaseAsync("Zorblax Quest IV", platformId)]);

        Resolution(scan.Candidate).ShouldBe("matched");
        scan.Candidate.GetProperty("needsArt").GetBoolean().ShouldBeFalse();
        Alternatives(scan.Candidate).ShouldAllBe(a => a.GetProperty("artworkUrl").ValueKind == JsonValueKind.Null);
        var response = await PostArtAsync(scan.Client, scan, Distance(AlternativeIds(scan.Candidate)[0], 0.1));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_not_found_item_is_not_a_close_call()
    {
        var library = await CreateLibraryAsync();
        using var client = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(client, library.Id);

        var candidate = (await IdentifyOkAsync(client, sessionId, Read(title: "Zorblax Quest IV", kind: "game")))[0];

        Resolution(candidate).ShouldBe("not_found");
        candidate.GetProperty("needsArt").GetBoolean().ShouldBeFalse();
        var unrelated = Guid.CreateVersion7();
        (await client.PostAsync(ArtUrl(sessionId, Id(candidate)), Json(new { distances = new[] { Distance(unrelated, 0.1) } })))
            .StatusCode.ShouldBe((HttpStatusCode)422);
    }

    [Fact]
    public async Task A_barcode_is_never_a_close_call()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(5701);
        await CatalogAsync("Star Fox 64", code);
        await CatalogAsync("Star Fox 64 Rumble Pak Edition", code); // a reused code: Ambiguous, but a picture cannot settle a code
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(client, library.Id);

        var candidate = (await ScanAsync(client, sessionId, code))[0];

        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("needsArt").GetBoolean().ShouldBeFalse();
        var ids = AlternativeIds(candidate);
        (await client.PostAsync(ArtUrl(sessionId, Id(candidate)), Json(new { distances = new[] { Distance(ids[0], 0.1) } })))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_barcode_candidate_is_refused_by_art_itself_even_if_its_stored_trace_claims_a_close_call()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(5702);
        await CatalogAsync("Star Fox 64", code);
        await CatalogAsync("Star Fox 64 Rumble Pak Edition", code);
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(client, library.Id);
        var candidate = (await ScanAsync(client, sessionId, code))[0];
        var ids = AlternativeIds(candidate);

        // Only the handler's own barcode check can stop this one: the stored flag says the pictures are wanted.
        await using (var context = Db.CreateContext())
        {
            var stored = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == Id(candidate)).Select(c => c.TraceJson).SingleAsync() ?? string.Empty;
            stored.ShouldMatch("\"needsArt\": ?false");
            await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == Id(candidate))
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.TraceJson, System.Text.RegularExpressions.Regex.Replace(stored, "\"needsArt\": ?false", "\"needsArt\": true")));
        }

        var response = await client.PostAsync(ArtUrl(sessionId, Id(candidate)), Json(new { distances = new[] { Distance(ids[0], 0.1) } }));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_close_calls_alternatives_carry_the_authorized_artwork_url_of_each_release_and_none_the_requester_cannot_see()
    {
        var other = await CreateLibraryAsync();
        var library = await CreateLibraryAsync();
        var platformId = await PlatformAsync();
        var plain = await ReleaseAsync("Pokemon Stadium", platformId);
        var hidden = await ReleaseAsync("Pokemon Stadium", platformId, edition: "Player's Choice", cover: false);
        var privateAsset = await CatalogSeed.AddAssetAsync(Db, $"covers/{Guid.NewGuid():N}.png", ownerLibraryId: other.Id, sha256: Guid.NewGuid().ToString("N"));
        await PointCoverAtAsync(hidden.Id, privateAsset); // visible release, but its picture belongs to another library
        var noCover = await ReleaseAsync("Pokemon Stadium", platformId, edition: "Greatest Hits", cover: false);
        using var client = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(client, library.Id);

        var candidate = (await IdentifyOkAsync(client, sessionId, Read(title: "Pokemon Stadium", kind: "game", platform: N64, year: 1999)))[0];

        candidate.GetProperty("needsArt").GetBoolean().ShouldBeTrue();
        var urls = Alternatives(candidate).ToDictionary(a => a.GetProperty("releaseId").GetGuid(), a => a.GetProperty("artworkUrl").GetString());
        urls.Count.ShouldBe(3);
        urls[plain.Id].ShouldBe($"/media/covers/{plain.CoverAssetId}"); // this server's own route, never a third party's
        urls[hidden.Id].ShouldBeNull();
        urls[noCover.Id].ShouldBeNull();
    }

    [Fact]
    public async Task The_close_call_margin_is_configuration()
    {
        // With no margin at all, only an Ambiguous item is a close call: a Matched one with a modest lead is not.
        ConfigureServices = services => services.AddSingleton<IOptionsMonitor<ScoringOptions>>(
            new FixedScoring(new ScoringOptions { Art = new ArtScoringOptions { CloseCallMargin = 0.0 } }));
        var scan = await ScanCloseCallAsync(
            "Pokemon Stadium",
            async platformId =>
                [await ReleaseAsync("Pokemon Stadium", platformId, edition: "Player's Choice"), await ReleaseAsync("Pokemon Stadium", platformId, edition: "Greatest Hits")],
            edition: "Player's Choice");

        Resolution(scan.Candidate).ShouldBe("matched");
        scan.Candidate.GetProperty("needsArt").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_distance_scale_is_configuration()
    {
        // A scale of 0 would erase the art term; a tiny one makes every distance count as "identical". Use 0.001: nothing separates the two.
        ConfigureServices = services => services.AddSingleton<IOptionsMonitor<ScoringOptions>>(
            new FixedScoring(new ScoringOptions { Art = new ArtScoringOptions { DistanceScale = 0.001 } }));
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);

        var candidate = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.1), Distance(ids[1], 1.3)));

        Resolution(candidate).ShouldBe("ambiguous");
    }

    [Fact]
    public async Task A_distance_above_the_configured_maximum_is_refused()
    {
        ConfigureServices = services => services.AddSingleton<IOptionsMonitor<ScoringOptions>>(
            new FixedScoring(new ScoringOptions { Art = new ArtScoringOptions { MaxDistance = 2.0 } }));
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var id = AlternativeIds(scan.Candidate)[0];

        (await PostArtAsync(scan.Client, scan, Distance(id, 2.0))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostArtAsync(scan.Client, scan, Distance(id, 2.5))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_spine_on_a_shelf_has_no_cover_to_compare_so_it_is_not_a_close_call()
    {
        var library = await CreateLibraryAsync();
        var platformId = await PlatformAsync();
        await ReleaseAsync("Pokemon Stadium", platformId);
        await ReleaseAsync("Pokemon Stadium", platformId, edition: "Player's Choice");
        using var client = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(client, library.Id);

        // Four items in one picture read as spines (ShelfSpine gives the art term no weight).
        var candidates = await IdentifyOkAsync(
            client, sessionId, Read(title: "Pokemon Stadium", kind: "game", platform: N64), Read(title: "Zorblax One", kind: "game"),
            Read(title: "Zorblax Two", kind: "game"), Read(title: "Zorblax Three", kind: "game"));

        Resolution(candidates[0]).ShouldBe("ambiguous");
        candidates[0].GetProperty("needsArt").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_body_is_limited_in_size_count_and_type()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        var six = Enumerable.Range(0, 6).Select(_ => Distance(Guid.CreateVersion7(), 0.1)).ToArray();
        (await PostArtAsync(scan.Client, scan, six)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostArtAsync(scan.Client, scan)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // none
        (await PostArtRawAsync(scan.Client, scan, "{}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostArtRawAsync(scan.Client, scan, "not json")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostArtRawAsync(scan.Client, scan, "null")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostArtRawAsync(scan.Client, scan, "{\"distances\":[null]}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostArtRawAsync(scan.Client, scan, $"{{\"distances\":[{{\"releaseId\":\"{ids[0]}\"}}]}}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostArtRawAsync(scan.Client, scan, $"{{\"distances\":[{{\"distance\":0.1}}]}}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var big = $"{{\"distances\":[{{\"releaseId\":\"{ids[0]}\",\"distance\":0.1}}],\"padding\":\"{new string('x', 5000)}\"}}";
        var tooLarge = await PostArtRawAsync(scan.Client, scan, big);
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        tooLarge.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var text = await scan.Client.PostAsync(ArtUrl(scan), new StringContent("distances", System.Text.Encoding.UTF8, "text/plain"));
        text.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
    }

    private async Task PointCoverAtAsync(Guid releaseId, Guid assetId)
    {
        await using var context = Db.CreateContext();
        await context.Releases.IgnoreQueryFilters().Where(r => r.Id == releaseId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.CoverAssetId, assetId));
    }

    private sealed class FixedScoring(ScoringOptions value)
        : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue => value;

        public ScoringOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
    }
}
