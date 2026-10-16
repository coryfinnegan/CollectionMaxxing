using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Artwork;

// LOCKED acceptance tests of SCAN-2b (issue #113): artwork distances from the phone can refine a close call, never force a
// wrong release through. Do not edit or weaken.
public class ArtworkLockedTests(ApiFixture api) : ArtworkTest(api)
{
    [Fact]
    public async Task Reported_distances_can_never_lift_a_release_outside_the_candidates_alternatives()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var platformId = await PlatformAsync("GameCube", "GCN");
        var stranger = await ReleaseAsync("Pokemon Stadium", platformId); // a real, visible release, but not one of this candidate's
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        // A perfect match for a release that is not an option, beside a poor one for a real option.
        var response = await PostArtAsync(scan.Client, scan, Distance(stranger.Id, 0.0), Distance(AlternativeIds(scan.Candidate)[0], 1.3));

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync());
        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
    }

    [Fact]
    public async Task Distances_cannot_push_a_candidate_over_the_matched_rule_when_the_platform_cap_applies()
    {
        // The read says Nintendo 64; the only releases are GameCube ones. The platform mismatch caps their score below Matched.
        var scan = await ScanCloseCallAsync(
            "Wave Race Blue Storm",
            async _ =>
            {
                var gamecube = await PlatformAsync("GameCube", "GCN");
                return [await ReleaseAsync("Wave Race Blue Storm", gamecube), await ReleaseAsync("Wave Race Blue Storm", gamecube, edition: "Player's Choice")];
            });
        Resolution(scan.Candidate).ShouldBe("ambiguous");
        var ids = AlternativeIds(scan.Candidate);

        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.0), Distance(ids[1], 1.4));

        var candidate = await ReadOkAsync(response);
        Resolution(candidate).ShouldNotBe("matched");
        Decision(candidate).ShouldBe("undecided");
        candidate.GetProperty("score").GetDouble().ShouldBeLessThan(0.90);
    }

    [Fact]
    public async Task Distances_cannot_push_a_candidate_over_the_matched_rule_when_the_title_year_cap_applies()
    {
        // The read is "Madden NFL 2005"; the catalogue has the 2004 game twice. A different year in the title is a known conflict.
        var scan = await ScanCloseCallAsync(
            "Madden NFL 2005",
            async platformId => [await ReleaseAsync("Madden NFL 2004", platformId), await ReleaseAsync("Madden NFL 2004", platformId, edition: "Greatest Hits")]);
        var ids = AlternativeIds(scan.Candidate);

        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.0), Distance(ids[1], 1.4));

        var candidate = await ReadOkAsync(response);
        Resolution(candidate).ShouldNotBe("matched");
        Decision(candidate).ShouldBe("undecided");
        candidate.GetProperty("score").GetDouble().ShouldBeLessThan(0.90);
    }

    [Fact]
    public async Task A_release_that_is_another_librarys_private_one_or_not_an_alternative_is_422_and_changes_nothing()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var other = await CreateLibraryAsync();
        var platformId = await PlatformAsync("GameCube", "GCN");
        var secret = await ReleaseAsync("Secret Prototype Of Hidden Title", platformId, ownerLibraryId: other.Id);
        var missing = Guid.CreateVersion7();
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        foreach (var unknown in new[] { secret.Id, missing })
        {
            var response = await PostArtAsync(scan.Client, scan, Distance(unknown, 0.1));
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe((HttpStatusCode)422, body);
            body.ShouldNotContain("Secret Prototype", Case.Insensitive);
            body.ShouldNotContain("Hidden Title", Case.Insensitive);
            body.ShouldNotContain(secret.CoverAssetId!.Value.ToString(), Case.Insensitive);
            (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
        }

        // One bad id among good ones spoils the whole request: nothing is scored.
        var mixed = await PostArtAsync(scan.Client, scan, Distance(AlternativeIds(scan.Candidate)[0], 0.1), Distance(secret.Id, 0.1));
        mixed.StatusCode.ShouldBe((HttpStatusCode)422);
        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
    }

    [Theory]
    [InlineData(LibraryRole.Viewer)]
    [InlineData(LibraryRole.Editor)]
    [InlineData(LibraryRole.Owner)]
    public async Task Art_is_open_to_the_sessions_own_starter_in_every_library_role(LibraryRole role)
    {
        var library = await CreateLibraryAsync();
        var platformId = await PlatformAsync();
        await ReleaseAsync("Pokemon Stadium", platformId);
        await ReleaseAsync("Pokemon Stadium", platformId, edition: "Player's Choice");
        using var client = As(library, role);
        var sessionId = await StartSessionAsync(client, library.Id);
        var candidate = (await IdentifyOkAsync(client, sessionId, Read(title: "Pokemon Stadium", kind: "game", platform: N64, year: 1999)))[0];
        var ids = AlternativeIds(candidate);

        var response = await client.PostAsync(
            ArtUrl(sessionId, Id(candidate)), Json(new { distances = new[] { Distance(ids[0], 0.05), Distance(ids[1], 1.3) } }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Art_is_404_for_another_librarys_or_another_users_session_or_candidate_and_401_when_signed_out()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var body = Json(new { distances = new[] { Distance(ids[0], 0.1) } });
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        var other = await CreateLibraryAsync();
        using var strangersOwner = As(other, LibraryRole.Owner);
        (await strangersOwner.PostAsync(ArtUrl(scan), body)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var outsider = AsUser(scan.Library.OutsiderId);
        (await outsider.PostAsync(ArtUrl(scan), Json(new { distances = new[] { Distance(ids[0], 0.1) } }))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var owner = As(scan.Library, LibraryRole.Owner); // outranks the editor, but the session is the editor's
        (await owner.PostAsync(ArtUrl(scan), Json(new { distances = new[] { Distance(ids[0], 0.1) } }))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Right session, a candidate that doesn't exist in it (or exists in another session).
        (await scan.Client.PostAsync(ArtUrl(scan.SessionId, Guid.CreateVersion7()), Json(new { distances = new[] { Distance(ids[0], 0.1) } })))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var otherSession = await StartSessionAsync(scan.Client, scan.Library.Id);
        (await scan.Client.PostAsync(ArtUrl(otherSession, scan.CandidateId), Json(new { distances = new[] { Distance(ids[0], 0.1) } })))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var anonymous = Factory.CreateClient();
        (await anonymous.PostAsync(ArtUrl(scan), Json(new { distances = new[] { Distance(ids[0], 0.1) } }))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("-0.5")]
    [InlineData("1e9")]
    [InlineData("1e999")]
    [InlineData("\"0.1\"")]
    [InlineData("null")]
    public async Task A_distance_that_is_not_finite_or_not_sane_is_rejected_with_a_problem_and_changes_nothing(string distance)
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var id = AlternativeIds(scan.Candidate)[0];
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        var response = await PostArtRawAsync(scan.Client, scan, $$"""{"distances":[{"releaseId":"{{id}}","distance":{{distance}}}]}""");

        ((int)response.StatusCode).ShouldBeInRange(400, 422);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
    }

    [Fact]
    public async Task Two_distances_for_one_release_are_rejected_with_a_problem_and_change_nothing()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        // The second entry would otherwise let the phone pick whichever of its own numbers suits it.
        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.1), Distance(ids[1], 1.3), Distance(ids[0], 1.0));

        ((int)response.StatusCode).ShouldBeInRange(400, 422);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);
    }

    [Fact]
    public async Task A_candidate_the_person_decided_answers_409_and_is_unchanged()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        (await DecideAsync(scan.Client, scan.SessionId, scan.CandidateId, new { releaseId = ids[1], decision = "accepted" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.05), Distance(ids[1], 1.3));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);

        // Rejecting is deciding too.
        var rejected = await ScanOriginalAndPlayersChoiceAsync("Banjo Kazooie");
        var rejectedIds = AlternativeIds(rejected.Candidate);
        (await DecideAsync(rejected.Client, rejected.SessionId, rejected.CandidateId, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rejectedBefore = await CandidateTextNowAsync(rejected.Client, rejected.SessionId, rejected.CandidateId);
        (await PostArtAsync(rejected.Client, rejected, Distance(rejectedIds[0], 0.05))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CandidateTextNowAsync(rejected.Client, rejected.SessionId, rejected.CandidateId)).ShouldBe(rejectedBefore);
    }

    [Fact]
    public async Task A_closed_session_answers_409_and_is_unchanged()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        (await CommitAsync(scan.Client, scan.SessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId);

        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 0.05), Distance(ids[1], 1.3));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await CandidateTextNowAsync(scan.Client, scan.SessionId, scan.CandidateId)).ShouldBe(before);

        var abandoned = await ScanOriginalAndPlayersChoiceAsync("Diddy Kong Racing");
        var abandonedIds = AlternativeIds(abandoned.Candidate);
        (await abandoned.Client.DeleteAsync(SessionUrl(abandoned.SessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PostArtAsync(abandoned.Client, abandoned, Distance(abandonedIds[0], 0.05))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
