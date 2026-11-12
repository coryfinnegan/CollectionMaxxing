using System.Net.Http.Headers;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning.Identify;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

/// <summary>A shelf reader that answers what a test tells it to, one picture at a time or held until the test lets it go, and counts what was asked of it.</summary>
public sealed class ScriptedReader : IShelfReader
{
    private int _calls;
    private int _inside;
    private int _mostInside;
    private int _cancelled;

    /// <summary>What the reader says for the n-th call (1 for the first).</summary>
    public Func<int, ShelfReadResult> Answer { get; set; } = _ => ShelfReadResult.Empty(ShelfReadStatus.Ok);

    /// <summary>Runs inside the read before it answers (a test holds the reader here); the token is the request's.</summary>
    public Func<int, CancellationToken, Task>? Hold { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The most reads that were ever inside the reader at once.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostInside);

    /// <summary>How many reads were told to stop because the request was dropped.</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    public async Task<ShelfReadResult> ReadAsync(byte[] jpeg, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        var inside = Interlocked.Increment(ref _inside);
        int seen;
        do
        {
            seen = Volatile.Read(ref _mostInside);
        }
        while (inside > seen && Interlocked.CompareExchange(ref _mostInside, inside, seen) != seen);

        try
        {
            if (Hold is { } hold)
            {
                await hold(call, cancellationToken);
            }

            return Answer(call);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }

    public static ShelfReadResult Found(params string[] titles) =>
        new([.. titles.Select(t => new ShelfReadItem(t, string.Empty, ShelfItemKind.Game))], ShelfReadStatus.Ok);
}

/// <summary>A still maker that hands the picture on as it is and counts how often it was asked.</summary>
public sealed class CountingStills : IShelfStillMaker
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public byte[]? Make(byte[] picture)
    {
        Interlocked.Increment(ref _calls);
        return picture;
    }
}

/// <summary>
/// Base of the <c>POST …/snaps/{snapId}:read-shelf</c> tests: a fake reader and still maker in place of the model, helpers to answer a snap the way the phone
/// does (identify) and to send its picture. Pictures are drawn by <see cref="JpegFixtures"/>; titles are neutral catalogue entries.
/// </summary>
public abstract class ShelfReadTest : IdentifyTest
{
    protected ShelfReadTest(ApiFixture api)
        : base(api)
    {
        AddServices(services =>
        {
            services.RemoveAll<IShelfReader>();
            services.AddSingleton<IShelfReader>(Reader);
            services.RemoveAll<IShelfStillMaker>();
            services.AddSingleton<IShelfStillMaker>(Stills);
            services.RemoveAll<IPhotoStore>();
            services.AddSingleton(Photos);
        });
        SetUpReader();
    }

    protected ScriptedReader Reader { get; } = new();

    protected CountingStills Stills { get; } = new();

    /// <summary>Every call to the photo store is recorded: a shelf read must make none.</summary>
    protected IPhotoStore Photos { get; } = Substitute.For<IPhotoStore>();

    /// <summary>Adds to whatever the test already set for the app's services, so helpers can be combined.</summary>
    protected void AddServices(Action<IServiceCollection> add)
    {
        var before = ConfigureServices;
        ConfigureServices = services =>
        {
            before?.Invoke(services);
            add(services);
        };
    }

    /// <summary>A reader endpoint is set up (never called: the fake replaces the reader).</summary>
    protected void SetUpReader() => Settings["ShelfReader:Endpoint"] = "http://127.0.0.1:1";

    protected void NoReader() => Settings.Remove("ShelfReader:Endpoint");

    protected static string ReadShelfUrl(Guid sessionId, Guid snapId) => $"{SessionUrl(sessionId)}/snaps/{snapId}:read-shelf";

    /// <summary>The phone's identify for a shelf snap: its reads become the rows the server's read is added beside.</summary>
    protected static async Task<Guid> IdentifyShelfSnapAsync(HttpClient client, Guid sessionId, string shotKind = "shelf_segment", params object[] items)
    {
        var snapId = Guid.NewGuid();
        var body = Body(snapId, items.Length == 0 ? [Read(title: "Placeholder Read", kind: "game")] : items);
        body["shotKind"] = shotKind;
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return snapId;
    }

    protected static MultipartFormDataContent Form(byte[]? picture, string[]? lines = null, string fileField = "file")
    {
        var form = new MultipartFormDataContent();
        if (picture is not null)
        {
            var file = new ByteArrayContent(picture);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, fileField, "shelf.jpg");
        }

        if (lines is not null)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(lines)), "lines");
        }

        return form;
    }

    protected static async Task<HttpResponseMessage> ReadShelfAsync(
        HttpClient client, Guid sessionId, Guid snapId, string[]? lines = null, byte[]? picture = null, CancellationToken cancellationToken = default)
    {
        using var form = Form(picture ?? JpegFixtures.Plain(), lines);
        return await client.PostAsync(ReadShelfUrl(sessionId, snapId), form, cancellationToken);
    }

    protected static async Task<List<JsonElement>> CandidatesOfAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    protected static async Task<string> ProblemCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString()!;

    protected static string ReleaseTitle(JsonElement candidate) => candidate.GetProperty("release").GetProperty("title").GetString()!;

    protected static string SourceOf(JsonElement candidate) => candidate.GetProperty("source").GetString()!;
}
