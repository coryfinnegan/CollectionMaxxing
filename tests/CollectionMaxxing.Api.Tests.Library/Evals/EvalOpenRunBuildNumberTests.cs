using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The phone reports the TestFlight build that reads a run. The PC keeps it in the open run, in the scored result files and on the report's
/// header, so the watcher can credit the run to the right build. A malformed value is dropped; a phone that sends none works as before.
/// </summary>
public class EvalOpenRunBuildNumberTests(ApiFixture api) : OpenRunTest(api)
{
    private static object BodyWith(object? buildNumber) => new
    {
        device = new Dictionary<string, object?>
        {
            ["model"] = "iPhone18,1",
            ["osVersion"] = "26.0",
            ["appVersion"] = "1.2.1",
            ["buildNumber"] = buildNumber,
        },
        configVersion = "cfg-test-1",
    };

    private static object BodyWithoutBuildNumber() => new
    {
        device = new { model = "iPhone18,1", osVersion = "26.0", appVersion = "1.2.1" },
        configVersion = "cfg-test-1",
    };

    private JsonElement RunJsonDevice()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Golden.Results, "open-run", "run.json")));
        return document.RootElement.GetProperty("device").Clone();
    }

    private async Task<(JsonElement Open, JsonElement Latest, string Report, string Dated)> OpenAndFinishAsync(object body)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin, body);
        var open = RunJsonDevice();
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);

        using var latest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest.json")));
        return (open, latest.RootElement.GetProperty("device").Clone(),
            await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md")),
            await File.ReadAllTextAsync(ResultFiles().Single()));
    }

    [Fact]
    public async Task A_build_number_is_kept_in_the_open_run_the_dated_and_latest_result_and_the_report_header()
    {
        var (open, latest, report, dated) = await OpenAndFinishAsync(BodyWith("150"));

        open.GetProperty("buildNumber").GetString().ShouldBe("150");
        latest.GetProperty("buildNumber").GetString().ShouldBe("150");
        using var datedDocument = JsonDocument.Parse(dated);
        datedDocument.RootElement.GetProperty("device").GetProperty("buildNumber").GetString().ShouldBe("150");
        report.Split('\n').First(l => l.StartsWith("Run ", StringComparison.Ordinal)).ShouldContain("app 1.2.1 build 150");
    }

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("abc")]
    [InlineData("123456789012345678901")]
    public async Task A_malformed_build_number_is_dropped_and_the_run_is_still_opened(string malformed)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await OpenOkAsync(admin, BodyWith(malformed));

        RunJsonDevice().TryGetProperty("buildNumber", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_from_an_older_phone_without_a_build_number_works_as_before_and_its_files_carry_none()
    {
        var (open, latest, report, _) = await OpenAndFinishAsync(BodyWithoutBuildNumber());

        open.TryGetProperty("buildNumber", out _).ShouldBeFalse();
        latest.TryGetProperty("buildNumber", out _).ShouldBeFalse();
        report.ShouldNotContain(" build ");
    }
}
