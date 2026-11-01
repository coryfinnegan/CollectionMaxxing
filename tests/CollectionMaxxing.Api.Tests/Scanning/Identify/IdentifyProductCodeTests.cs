using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A stylised cartridge label defeats both the text reader and the on-device model ("Wrangler's 2" for Clay Fighter), but the
// product code printed on it (SNS-8C-USA) is plain text and the server's bundled table (ADR-031) says which game it belongs to.
// The server identifies the read as that game; with no code, or one that does not resolve, the read is scored as it always was.
public class IdentifyProductCodeTests(ApiFixture api) : IdentifyTest(api)
{
    private const string SuperNintendo = "SUPER NINTENDO";

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync()
    {
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        Games.Results.Add(ScanData.Game("Clay Fighter", "Super Nintendo Entertainment System", 1993, "g-clay-fighter"));
        return (client, await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

    private static Dictionary<string, object?> GarbledRead(string? productCode, string? platform = SuperNintendo)
    {
        var read = Read(title: "Wrangler's 2", platform: platform);
        read["productCode"] = productCode;
        return read;
    }

    private async Task<string?> TraceOfAsync(JsonElement candidate)
    {
        await using var context = Db.CreateContext();
        var row = await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(candidate));
        return row.TraceJson;
    }

    [Fact]
    public async Task A_garbled_title_with_its_product_code_finds_the_game_on_that_platform()
    {
        var (client, sessionId) = await StartAsync();

        var candidate = (await IdentifyOkAsync(client, sessionId, GarbledRead("SNS-8C-USA"))).Single();

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("release").GetProperty("title").GetString().ShouldBe("Clay Fighter");
    }

    [Fact]
    public async Task The_same_garbled_read_without_the_code_does_not_find_the_game()
    {
        var (client, sessionId) = await StartAsync();

        var candidate = (await IdentifyOkAsync(client, sessionId, GarbledRead(productCode: null))).Single();

        Resolution(candidate).ShouldNotBe("matched");
        candidate.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("sns 8c-usa")]
    [InlineData("SNS_8C_USA")]
    [InlineData("SNS-8C")]
    public async Task The_code_is_found_however_the_ocr_spelled_it(string code)
    {
        var (client, sessionId) = await StartAsync();

        var candidate = (await IdentifyOkAsync(client, sessionId, GarbledRead(code))).Single();

        Resolution(candidate).ShouldBe("matched");
    }

    [Fact]
    public async Task A_code_that_belongs_to_another_platform_than_the_read_names_decides_nothing()
    {
        var (client, sessionId) = await StartAsync();

        var candidate = (await IdentifyOkAsync(client, sessionId, GarbledRead("SNS-8C-USA", platform: "Nintendo 64"))).Single();

        Resolution(candidate).ShouldNotBe("matched");
    }

    [Fact]
    public async Task A_code_nothing_knows_leaves_the_read_as_it_was()
    {
        var (client, sessionId) = await StartAsync();

        var wrong = (await IdentifyOkAsync(client, sessionId, GarbledRead("SNS-ZZZZ-USA"))).Single();
        var rightRead = Read(title: "Clay Fighter", kind: "game", platform: SuperNintendo);
        rightRead["productCode"] = "SNS-ZZZZ-USA";
        var right = (await IdentifyOkAsync(client, sessionId, rightRead)).Single();

        Resolution(wrong).ShouldNotBe("matched"); // a code that names nothing gives no title: scored as read
        Resolution(right).ShouldBe("matched"); // and a right title is not spoiled by it
        right.GetProperty("release").GetProperty("title").GetString().ShouldBe("Clay Fighter");
    }

    [Fact]
    public async Task The_card_still_shows_what_was_read_and_the_stored_read_and_trace_say_a_code_decided()
    {
        var (client, sessionId) = await StartAsync();

        var candidate = (await IdentifyOkAsync(client, sessionId, GarbledRead("SNS-8C-USA"))).Single();

        candidate.GetProperty("visibleText").GetString().ShouldBe("Wrangler's 2");
        var inferred = candidate.GetProperty("inferred");
        inferred.GetProperty("title").GetString().ShouldBe("Clay Fighter");
        inferred.GetProperty("kind").GetString().ShouldBe("game");
        inferred.GetProperty("platform").GetString().ShouldBe("SNES");
        inferred.GetProperty("productCode").GetString().ShouldBe("SNS-8C-USA");
        inferred.GetProperty("readAs").GetString().ShouldBe("Wrangler's 2");

        var trace = await TraceOfAsync(candidate);
        trace.ShouldNotBeNull();
        trace.ShouldContain("product_code");
        trace.ShouldContain("SNS-8C-USA = Clay Fighter (SNES); read as: Wrangler's 2");
    }

    [Fact]
    public async Task A_read_without_a_code_has_no_code_step_in_its_trace()
    {
        var (client, sessionId) = await StartAsync();

        var candidate = (await IdentifyOkAsync(client, sessionId, Read(title: "Clay Fighter", kind: "game", platform: SuperNintendo))).Single();

        (await TraceOfAsync(candidate))!.ShouldNotContain("product_code");
        candidate.GetProperty("inferred").TryGetProperty("productCode", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_that_ran_out_of_time_is_resolved_later_by_the_code_it_was_stored_with()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var (client, sessionId) = await StartAsync();
        var slow = true;
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var pending = (await IdentifyOkAsync(client, sessionId, GarbledRead("SNS-8C-USA"))).Single();
        Resolution(pending).ShouldBe("pending");

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(client, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched");
        resolved.GetProperty("release").GetProperty("title").GetString().ShouldBe("Clay Fighter");
        (await TraceOfAsync(resolved))!.ShouldContain("product_code");
    }

    [Fact]
    public async Task A_bound_cover_read_with_a_code_upgrades_its_row_to_the_game()
    {
        var (client, sessionId) = await StartAsync();
        var row = (await IdentifyOkAsync(client, sessionId, Read(title: "A Game That Is Nowhere In The Catalogue")))[0];
        Resolution(row).ShouldNotBe("matched");

        var bound = Body(Guid.NewGuid(), [GarbledRead("SNS-8C-USA")]);
        bound["kind"] = "cover";
        bound["shotKind"] = "single_cover";
        bound["candidateId"] = Id(row);
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(bound));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var upgraded = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        Id(upgraded).ShouldBe(Id(row));
        upgraded.GetProperty("release").GetProperty("title").GetString().ShouldBe("Clay Fighter");
    }

    [Fact]
    public async Task The_eval_grounder_identifies_a_read_by_its_code_the_way_identify_does()
    {
        Games.Results.Add(ScanData.Game("Clay Fighter", "Super Nintendo Entertainment System", 1993, "g-clay-fighter"));
        using var scope = Factory.Services.CreateScope();
        var grounder = scope.ServiceProvider.GetRequiredService<EvalGrounder>();
        var read = new IdentifyItemInput(null, null, "WRANGLER'S 2", "Wrangler's 2", null, null, null, null, null, null, SuperNintendo, null, "SNS-8C-USA");

        var items = await grounder.GroundAsync(
            ScanEngine.VisionPipeline, [new EvalReadItemInput(read, null)], new EvalGroundingMemo(), printKind: null, CancellationToken.None);

        var item = items.ShouldHaveSingleItem();
        item.Verdict.ShouldBe("matched");
        item.Best!.Title.ShouldBe("Clay Fighter");
        item.Read!.Platform.ShouldBe("SNES");
    }

    // ---- validation -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("not a code!")]
    [InlineData("12345")]
    [InlineData("SNS-ADNE-USA-AND-A-GREAT-MANY-MORE-THINGS")]
    [InlineData("SNS-8C-USA                    EXTRA")]
    public async Task A_product_code_that_is_not_shaped_like_one_or_is_too_long_is_refused(string code)
    {
        var (client, sessionId) = await StartAsync();

        var response = await IdentifyAsync(client, sessionId, null, GarbledRead(code));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("scan.product_code_invalid");
        (await CountCandidatesAsync(sessionId)).ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_product_code_is_no_code(string code)
    {
        var (client, sessionId) = await StartAsync();

        var candidate = (await IdentifyOkAsync(client, sessionId, GarbledRead(code))).Single();

        Resolution(candidate).ShouldNotBe("matched");
    }
}
