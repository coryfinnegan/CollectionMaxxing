using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED acceptance tests of SCAN-1a (issue #61): a retried or doubled submit never doubles a candidate. Do not edit or weaken.
public class ScanIdempotencyTests(ApiFixture api) : ScanTest(api)
{
    private async Task<int> CountCandidatesAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().CountAsync(c => c.SessionId == sessionId);
    }

    [Fact]
    public async Task Resubmitting_a_batch_with_the_same_idempotency_key_returns_the_same_candidates_and_creates_no_duplicates()
    {
        var library = await CreateLibraryAsync();
        var first = ScanData.Upc(4001);
        var second = ScanData.Upc(4002);
        await CatalogAsync("Paper Mario", first);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var one = await SubmitAsync(viewer, sessionId, "batch-1", Code(first), Code(second));
        var two = await SubmitAsync(viewer, sessionId, "batch-1", Code(first), Code(second));

        one.StatusCode.ShouldBe(HttpStatusCode.OK);
        two.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ids = (await ReadJsonAsync(one)).GetProperty("candidates").EnumerateArray().Select(Id).ToList();
        var again = (await ReadJsonAsync(two)).GetProperty("candidates").EnumerateArray().Select(Id).ToList();
        ids.Count.ShouldBe(2);
        again.ShouldBe(ids);
        (await CountCandidatesAsync(sessionId)).ShouldBe(2);
    }

    [Fact]
    public async Task The_same_idempotency_key_with_a_different_batch_is_refused()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        (await SubmitAsync(viewer, sessionId, "batch-2", Code(ScanData.Upc(4010)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await SubmitAsync(viewer, sessionId, "batch-2", Code(ScanData.Upc(4011)));

        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_code_scanned_again_in_a_session_is_the_same_candidate()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(4020);
        await CatalogAsync("Kirby 64", code);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var first = await ScanAsync(viewer, sessionId, code);
        var again = await ScanAsync(viewer, sessionId, ScanData.Gtin13(code)); // the same product, written as an EAN-13

        Id(again[0]).ShouldBe(Id(first[0]));
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_code_twice_in_one_batch_is_one_candidate_with_both_copies()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(4021);
        await CatalogAsync("Kirby 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var response = await SubmitAsync(editor, sessionId, "batch-twice", Code(code), Code(code, copies: 2));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates");
        candidates.GetArrayLength().ShouldBe(1);
        candidates[0].GetProperty("copies").GetInt32().ShouldBe(3);
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_concurrent_identical_submits_create_one_candidate()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(4030);
        await CatalogAsync("Star Fox 64", code);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SubmitAsync(viewer, sessionId, "same-key", Code(code))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            foreach (var candidate in (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray())
            {
                ids.Add(Id(candidate));
            }
        }

        ids.Count.ShouldBe(1);
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_submits_of_the_same_code_under_different_keys_create_one_candidate()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(4031);
        await CatalogAsync("Star Fox 64", code);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => SubmitAsync(viewer, sessionId, $"key-{i}", Code(code))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_batches_of_different_codes_get_distinct_sequence_numbers()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => SubmitAsync(viewer, sessionId, $"k{i}", Code(ScanData.Upc(4100 + i)))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var session = await GetSessionAsync(viewer, sessionId);
        var seqs = session.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("seq").GetInt32()).ToList();
        seqs.Count.ShouldBe(8);
        seqs.Distinct().Count().ShouldBe(8);
        seqs.ShouldBe(seqs.Order().ToList()); // listed in order
    }
}
