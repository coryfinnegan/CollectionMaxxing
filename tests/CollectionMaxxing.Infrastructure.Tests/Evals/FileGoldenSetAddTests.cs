using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Tests.Evals;

/// <summary>
/// Adding a golden picture to the folder on the PC: the file is created and never replaced, an id stays unique across both sets, and the
/// write can only land in a plain <c>shelf</c> or <c>single</c> folder inside the golden folder.
/// </summary>
public sealed class FileGoldenSetAddTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    private static readonly byte[] Bytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

    public void Dispose() => _golden.Dispose();

    private FileGoldenSet Set(bool enabled = true, string? path = null) =>
        new(Options.Create(new EvalsOptions { Enabled = enabled, GoldenPath = path ?? _golden.Golden }));

    [Fact]
    public void A_picture_is_written_whole_as_id_dot_extension_in_its_set_folder()
    {
        var result = Set().Add("shelf", "shelf-01", GoldenPhotoFormat.Jpeg, Bytes);

        result.Outcome.ShouldBe(GoldenAddOutcome.Added);
        result.Photo.ShouldBe(new GoldenPhotoDto("shelf-01", "shelf", false, Bytes.Length));
        File.ReadAllBytes(Path.Combine(_golden.Golden, "shelf", "shelf-01.jpg")).ShouldBe(Bytes);
        SyntheticGolden.Files(_golden.Golden).ShouldBe(["shelf" + Path.DirectorySeparatorChar + "shelf-01.jpg"]);
    }

    [Fact]
    public void A_heic_is_written_with_the_heic_extension()
    {
        Set().Add("single", "cover-01", GoldenPhotoFormat.Heic, Bytes).Outcome.ShouldBe(GoldenAddOutcome.Added);

        File.Exists(Path.Combine(_golden.Golden, "single", "cover-01.heic")).ShouldBeTrue();
    }

    [Fact]
    public void The_added_picture_is_listed_and_can_be_opened_by_its_id()
    {
        var set = Set();
        set.Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes);

        set.List().ShouldBe([new GoldenPhotoDto("cover-01", "single", false, Bytes.Length)]);
        set.SetOf("cover-01").ShouldBe("single");
        using var image = set.OpenImage("cover-01")!.Content;
        image.Length.ShouldBe(Bytes.Length);
    }

    [Fact]
    public void An_existing_picture_is_never_replaced()
    {
        var existing = _golden.AddSingle("cover-01", null);
        var before = File.ReadAllBytes(existing);

        var result = Set().Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes);

        result.Outcome.ShouldBe(GoldenAddOutcome.IdTaken);
        result.Photo.ShouldBeNull();
        File.ReadAllBytes(existing).ShouldBe(before);
    }

    [Theory]
    [InlineData("shelf", "cover-01.jpg")]
    [InlineData("shelf", "cover-01.jpeg")]
    [InlineData("shelf", "cover-01.heic")]
    [InlineData("shelf", "cover-01.png")]
    [InlineData("shelf", "cover-01.truth.json")]
    [InlineData("shelf", "cover-01.truth.draft.json")]
    [InlineData("shelf", "Cover-01.JPG")]
    [InlineData("single", "cover-01.heic")]
    [InlineData("single", "COVER-01.truth.json")]
    public void An_id_in_either_set_with_any_extension_or_letter_case_is_taken(string existingSet, string existingFile)
    {
        _golden.AddFile(existingSet, existingFile, "x");

        Set().Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.IdTaken);
        Set().Add("shelf", "cover-01", GoldenPhotoFormat.Heic, Bytes).Outcome.ShouldBe(GoldenAddOutcome.IdTaken);
        SyntheticGolden.Files(_golden.Golden).ShouldBe([existingSet + Path.DirectorySeparatorChar + existingFile]);
    }

    [Fact]
    public void A_folder_named_like_the_id_also_counts_as_taken()
    {
        Directory.CreateDirectory(Path.Combine(_golden.Golden, "single", "cover-01.jpg"));

        Set().Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.IdTaken);
    }

    [Fact]
    public void A_name_that_only_starts_like_an_id_is_not_taken()
    {
        _golden.AddSingle("cover-011", null);
        _golden.AddShelf("cove", null);

        Set().Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.Added);
    }

    [Fact]
    public void A_set_folder_that_is_missing_is_made()
    {
        Directory.Delete(Path.Combine(_golden.Golden, "single"));

        Set().Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.Added);
        File.Exists(Path.Combine(_golden.Golden, "single", "cover-01.jpg")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("single")]
    [InlineData("shelf")]
    public void A_set_folder_that_is_a_link_is_not_written_through(string set)
    {
        var outside = Path.Combine(_golden.Root, "outside");
        Directory.CreateDirectory(outside);
        var folder = Path.Combine(_golden.Golden, set);
        Directory.Delete(folder);
        FolderLink.Create(folder, outside);

        Set().Add(set, "cover-01", GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.NotWritable);
        Directory.GetFileSystemEntries(outside).ShouldBeEmpty();
    }

    [Fact]
    public void A_set_folder_that_is_a_file_is_not_writable()
    {
        var folder = Path.Combine(_golden.Golden, "single");
        Directory.Delete(folder);
        File.WriteAllText(folder, "a file where the folder should be");

        Set().Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.NotWritable);
    }

    [Theory]
    [InlineData("other", "cover-01")]
    [InlineData("SINGLE", "cover-01")]
    [InlineData("..", "cover-01")]
    [InlineData("single", "..")]
    [InlineData("single", "..\\x")]
    [InlineData("single", "../x")]
    [InlineData("single", "a b")]
    [InlineData("single", "")]
    [InlineData("single", "c1.jpg")]
    [InlineData("single", "con")]
    public void A_set_or_id_that_is_not_plain_is_never_turned_into_a_path(string set, string id)
    {
        Set().Add(set, id, GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.NotWritable);
        SyntheticGolden.Files(_golden.Root).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, "golden")]
    [InlineData(true, "missing")]
    [InlineData(true, "")]
    public void With_the_evals_off_or_no_golden_folder_nothing_is_written_and_the_answer_is_unavailable(bool enabled, string folder)
    {
        var path = folder switch
        {
            "golden" => _golden.Golden,
            "missing" => Path.Combine(_golden.Root, "no-such-folder"),
            _ => string.Empty,
        };

        Set(enabled, path).Add("single", "cover-01", GoldenPhotoFormat.Jpeg, Bytes).Outcome.ShouldBe(GoldenAddOutcome.Unavailable);
        SyntheticGolden.Files(_golden.Root).ShouldBeEmpty();
        Directory.Exists(Path.Combine(_golden.Root, "no-such-folder")).ShouldBeFalse();
    }

    [Fact]
    public async Task The_same_id_added_at_once_to_both_sets_lands_in_one_of_them()
    {
        var set = Set();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => Task.Run(() => set.Add(i % 2 == 0 ? "single" : "shelf", "same-01", GoldenPhotoFormat.Jpeg, Bytes))));

        results.Count(r => r.Outcome == GoldenAddOutcome.Added).ShouldBe(1);
        results.Count(r => r.Outcome == GoldenAddOutcome.IdTaken).ShouldBe(7);
        set.List().Count.ShouldBe(1);
    }
}
