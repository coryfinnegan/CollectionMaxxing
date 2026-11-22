using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

/// <summary>A warm-up of the shelf reader (#832) never takes a shelf read's place: the real warmer talks to a WireMock reader that is held mid-load.</summary>
public class ShelfReadWarmUpTests : ShelfReadTest
{
    private readonly WireMockServer _reader = LoopbackWireMock.Start();

    private readonly ManualResetEventSlim _arrived = new(false);

    private readonly ManualResetEventSlim _release = new(false);

    public ShelfReadWarmUpTests(ApiFixture api)
        : base(api)
    {
        Settings["ShelfReader:Endpoint"] = _reader.Url!;
        Settings["ShelfReader:QueueMaxWaiting"] = "0"; // any read that does not get the card at once is told the reader is busy
        Settings["ShelfReader:QueueWaitSeconds"] = "0";
        _reader.Given(Request.Create().WithPath("/v1/chat/completions").UsingPost()).RespondWith(Response.Create().WithCallback(_ =>
        {
            _arrived.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
            return new WireMock.ResponseMessage { StatusCode = 200 };
        }));
    }

    [Fact]
    public async Task A_warm_up_never_takes_the_reads_place_so_a_shelf_read_during_one_is_not_told_the_reader_is_busy()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(9501));
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);
        var snap = await IdentifyShelfSnapAsync(client, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", year: 1996));
        Reader.Answer = _ => ScriptedReader.Found("Star Fox 64");

        (await client.PostAsync("/api/v1/recognition/shelf-reader:warm", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _arrived.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue("the warm-up reached the reader");

        var read = await ReadShelfAsync(client, sessionId, snap);

        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        _release.Set();
    }

    public override async ValueTask DisposeAsync()
    {
        _release.Set();
        _reader.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
