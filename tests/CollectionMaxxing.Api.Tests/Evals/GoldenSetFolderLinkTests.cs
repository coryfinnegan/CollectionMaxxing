using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// A <c>shelf</c> or <c>single</c> folder that is itself a link (a junction, made without admin rights on Windows) must not be followed:
/// otherwise it serves files from outside the golden folder and receives the truth drafts.
/// </summary>
public class GoldenSetFolderLinkTests(ApiFixture api) : EvalTest(api)
{
    private static readonly SyntheticGolden.SingleItem CartTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private static readonly byte[] OutsideBytes = Encoding.ASCII.GetBytes("OUTSIDE-THE-GOLDEN-FOLDER");

    /// <summary>Replaces the set folder with a junction to a folder outside the golden folder that holds a picture with truth and a picture without.</summary>
    private string LinkSetToOutsideFolder(string set)
    {
        var outside = Path.Combine(Golden.Root, "outside-" + set);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "o1.jpg"), OutsideBytes);
        File.WriteAllText(
            Path.Combine(outside, "o1.truth.json"),
            JsonSerializer.Serialize(new { title = "Outside Title", format = "game_cartridge", completeness = "cart_only", kind = "loose_cartridge" }));
        File.WriteAllBytes(Path.Combine(outside, "o2.jpg"), OutsideBytes);

        var folder = Path.Combine(Golden.Golden, set);
        Directory.Delete(folder, true);
        var script = Path.Combine(Golden.Root, "link.ps1");
        File.WriteAllText(script, $"New-Item -ItemType Junction -Path '{folder}' -Target '{outside}' | Out-Null");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, "could not create the junction");
        new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint).ShouldBeTrue();
        return outside;
    }

    [Theory]
    [InlineData("single")]
    [InlineData("shelf")]
    public async Task A_set_folder_that_is_a_junction_to_outside_serves_nothing_and_receives_no_drafts(string set)
    {
        Xunit.Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs a Windows junction.");
        Golden.AddSingle("c1", CartTruth);
        var outside = LinkSetToOutsideFolder(set);
        using var admin = AdminClient();

        var list = await admin.GetStringAsync($"{Base}/golden");
        using var image = await admin.GetAsync($"{Base}/golden/o1/image");
        using var response = await PostRunAsync(admin, RunBody(PhotoResult("o2", "apple_model", [ReadItem("Star Fox 64")])));

        list.ShouldNotContain("o1");
        list.ShouldNotContain("o2");
        image.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await image.Content.ReadAsStringAsync()).ShouldNotContain("OUTSIDE");
        ((int)response.StatusCode).ShouldBeLessThan(500);
        Directory.GetFiles(outside).Select(Path.GetFileName).Order().ShouldBe(["o1.jpg", "o1.truth.json", "o2.jpg"]);
    }
}
