using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

/// <summary>
/// An interrupted <c>/identify</c> request never leaves an item pending forever, and the size limits hold to the byte.
/// Serial: the client gives up after 800 ms, and the request must have reached the server and stored its candidate by then, which a busy
/// machine can overrun on a fresh host.
/// </summary>
[Collection(RunsAlone.Name)]
public class IdentifyReliabilityTests(ApiFixture api) : IdentifyTest(api)
{
    private const int MaxItemBytes = 16 * 1024;
    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>The queue is shared by every test, so other tests' jobs may be ahead of ours: run jobs until our candidate leaves pending.</summary>
    private async Task RunJobsUntilSettledAsync(HttpClient client, Guid sessionId)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Resolution((await GetSessionAsync(client, sessionId)).GetProperty("candidates")[0]) != "pending")
            {
                return;
            }

            await RunNextJobAsync();
        }
    }

    private async Task<(HttpClient Client, Guid SessionId, Guid SnapId)> AbortedFirstAttemptAsync(bool providerRecovers)
    {
        Options.ResolveBudget = TimeSpan.FromSeconds(30); // long enough that the client gives up first
        Options.JobResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var hanging = true;
        Games.Results.Add(ScanData.Game("Zorblax Quest IV", "Nintendo 64", 1998, "g-zorblax"));
        Games.Behaviour = async ct =>
        {
            if (hanging)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var snapId = Guid.NewGuid();

        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await editor.PostAsync(IdentifyUrl(sessionId), Json(Body(snapId, [Read(title: "Zorblax Quest IV", kind: "game")])), abort.Token));

        // Let the server notice the abort and finish unwinding.
        await Task.Delay(500);
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
        hanging = !providerRecovers;
        return (editor, sessionId, snapId);
    }

    [Fact]
    public async Task A_request_cut_off_mid_lookup_is_finished_by_the_job_after_the_retry()
    {
        var (editor, sessionId, snapId) = await AbortedFirstAttemptAsync(providerRecovers: true);
        using var _ = editor;

        var retry = await IdentifyAsync(editor, sessionId, snapId, Read(title: "Zorblax Quest IV", kind: "game"));
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);

        await RunJobsUntilSettledAsync(editor, sessionId);

        var stored = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(stored).ShouldBe("matched");
    }

    [Fact]
    public async Task A_request_cut_off_mid_lookup_never_leaves_the_item_pending_even_when_the_provider_stays_down()
    {
        var (editor, sessionId, snapId) = await AbortedFirstAttemptAsync(providerRecovers: false);
        using var _ = editor;

        (await IdentifyAsync(editor, sessionId, snapId, Read(title: "Zorblax Quest IV", kind: "game"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsUntilSettledAsync(editor, sessionId);

        var stored = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(stored).ShouldBe("error");
    }

    [Theory]
    [InlineData(true, "not_found")]
    [InlineData(false, "error")]
    public async Task A_barcode_batch_cut_off_mid_lookup_is_finished_by_the_job_after_the_retry(bool providerRecovers, string expected)
    {
        Options.ResolveBudget = TimeSpan.FromSeconds(30);
        Options.JobResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var hanging = true;
        Products.Behaviour = async (_, ct) =>
        {
            if (hanging)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return ProductLookupResult.NotFound;
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var code = Code(ScanData.Upc(9101));

        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/barcodes") { Content = Json(new { codes = new[] { code } }) };
            request.Headers.Add("Idempotency-Key", "cut-off");
            await editor.SendAsync(request, abort.Token);
        });
        await Task.Delay(500);
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
        hanging = !providerRecovers;

        (await SubmitAsync(editor, sessionId, "cut-off", code)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsUntilSettledAsync(editor, sessionId);

        Resolution((await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0]).ShouldBe(expected);
    }

    [Fact]
    public async Task A_snap_id_reused_by_another_user_in_the_same_library_is_a_conflict_and_shows_nothing()
    {
        var library = await CreateLibraryAsync();
        using var owner = As(library, LibraryRole.Owner);
        using var editor = As(library, LibraryRole.Editor);
        var snapId = Guid.NewGuid();
        var ownersSession = await StartSessionAsync(owner, library.Id);
        (await IdentifyAsync(owner, ownersSession, snapId, Read(title: "Theirs", kind: "game"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var editorsSession = await StartSessionAsync(editor, library.Id);

        var response = await IdentifyAsync(editor, editorsSession, snapId, Read(title: "Mine", kind: "game"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("Theirs");
        (await CountCandidatesAsync(editorsSession)).ShouldBe(0);
    }

    [Fact]
    public async Task An_item_of_exactly_16_kilobytes_is_accepted_and_one_byte_more_is_refused()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        const string localId = "abcd1234";

        var atLimit = Read(title: "Edge", text: new string('x', MaxItemBytes - localId.Length - "Edge".Length), kind: "book", localId: localId);
        (await IdentifyAsync(viewer, sessionId, null, atLimit)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var over = Read(title: "Edge", text: new string('x', MaxItemBytes - localId.Length - "Edge".Length + 1), kind: "book", localId: localId);
        (await IdentifyAsync(viewer, sessionId, null, over)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_body_of_exactly_256_kilobytes_is_read_and_one_byte_more_is_413(bool withContentLength)
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        (await PostPaddedAsync(viewer, sessionId, MaxBodyBytes, withContentLength)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostPaddedAsync(viewer, sessionId, MaxBodyBytes + 1, withContentLength)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    private static async Task<HttpResponseMessage> PostPaddedAsync(HttpClient client, Guid sessionId, int totalBytes, bool withContentLength)
    {
        var json = JsonSerializer.Serialize(Body(Guid.NewGuid(), [Read(title: "Pad", kind: "book")]));
        var bytes = Encoding.UTF8.GetBytes(json.PadRight(totalBytes)); // trailing spaces are valid JSON
        bytes.Length.ShouldBe(totalBytes);
        using HttpContent content = withContentLength ? new ByteArrayContent(bytes) : new UnsizedContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await client.PostAsync(IdentifyUrl(sessionId), content);
    }

    /// <summary>Content that cannot say how long it is, so the request goes out chunked with no Content-Length.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
