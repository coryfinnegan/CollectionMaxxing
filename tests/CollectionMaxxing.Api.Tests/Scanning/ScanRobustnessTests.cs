using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED acceptance tests of SCAN-1a (issue #61): bad codes and flaky providers never fail a batch. Do not edit or weaken.
public class ScanRobustnessTests(ApiFixture api) : ScanTest(api)
{
    [Fact]
    public async Task A_garbage_or_checksum_failing_code_is_a_per_code_error_and_does_not_fail_the_other_codes()
    {
        var library = await CreateLibraryAsync();
        var good = ScanData.Upc(5001);
        await CatalogAsync("Mario Party", good);
        var badChecksum = good[..^1] + (good[^1] == '9' ? '0' : (char)(good[^1] + 1));
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await SubmitAsync(
            viewer, sessionId, "mixed", Code("not a barcode!"), Code(badChecksum), Code(good), Code("12345"), Code(string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
        candidates.Count.ShouldBe(5);
        Resolution(candidates[0]).ShouldBe("error");
        Resolution(candidates[1]).ShouldBe("error");
        Resolution(candidates[2]).ShouldBe("matched");
        Resolution(candidates[3]).ShouldBe("error");
        Resolution(candidates[4]).ShouldBe("error");
        Decision(candidates[2]).ShouldBe("accepted");
        candidates.Where((_, i) => i != 2).ShouldAllBe(c => Decision(c) == "undecided");
    }

    [Fact]
    public async Task A_garbage_code_does_not_reach_any_provider()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        (await SubmitAsync(viewer, sessionId, "garbage", Code("zzzz"), Code("9999999999999"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        Products.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task More_than_50_codes_or_none_at_all_is_a_validation_problem()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var tooMany = await SubmitAsync(viewer, sessionId, "many", Enumerable.Range(0, 51).Select(i => Code(ScanData.Upc(6000 + i))).ToArray());
        var none = await SubmitAsync(viewer, sessionId, "none");
        var fifty = await SubmitAsync(viewer, sessionId, "fifty", Enumerable.Range(0, 50).Select(i => Code(ScanData.Upc(6100 + i))).ToArray());

        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        none.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        fifty.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public async Task Copies_outside_1_to_99_is_a_validation_problem(int copies)
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await SubmitAsync(viewer, sessionId, "copies", Code(ScanData.Upc(6200), copies));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_a_validation_problem()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await SubmitAsync(viewer, sessionId, " ", Code(ScanData.Upc(6201)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_provider_that_times_out_fails_or_errors_leaves_only_that_code_pending_or_error_never_the_request()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(600);
        var library = await CreateLibraryAsync();
        var throws = ScanData.Upc(7001);
        var hangs = ScanData.Upc(7002);
        var unavailable = ScanData.Upc(7003);
        var quota = ScanData.Upc(7004);
        var fine = ScanData.Upc(7005);
        await CatalogAsync("Yoshi's Story", fine);
        Products.Behaviour = async (id, ct) =>
        {
            if (id.Value == ScanData.Gtin13(throws))
            {
                throw new HttpRequestException("boom: 503 Service Unavailable");
            }

            if (id.Value == ScanData.Gtin13(hangs))
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }

            if (id.Value == ScanData.Gtin13(unavailable))
            {
                return ProductLookupResult.Unavailable;
            }

            return id.Value == ScanData.Gtin13(quota) ? ProductLookupResult.QuotaExhausted : ProductLookupResult.NotFound;
        };
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await SubmitAsync(viewer, sessionId, "flaky", Code(throws), Code(hangs), Code(unavailable), Code(quota), Code(fine));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
        candidates.Count.ShouldBe(5);
        foreach (var candidate in candidates.Take(4))
        {
            Resolution(candidate).ShouldBeOneOf("pending", "error");
            Decision(candidate).ShouldBe("undecided");
        }

        Resolution(candidates[4]).ShouldBe("matched"); // the good code is not held up by the bad ones
    }

    [Fact]
    public async Task A_book_provider_that_fails_leaves_the_isbn_pending_or_error()
    {
        var library = await CreateLibraryAsync();
        Books.Behaviour = _ => throw new HttpRequestException("boom: 500");
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await ScanAsync(viewer, sessionId, ScanData.Isbn);

        Resolution(candidates[0]).ShouldBeOneOf("pending", "error");
    }
}
