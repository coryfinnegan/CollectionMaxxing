using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The app sends one identify per snap holding both engines' reads, but sends only the model reads that stand beside Vision's: the
/// ones the shelf's print backs, or that sit on a Vision read. Only those vote on the shot's kind, so the eval's snap-wide kind
/// counts the same reads the combined row does.
/// </summary>
public sealed class StandingModelKindsTests
{
    private static GroundedPhoto ModelRead(params GroundedItem[] items) => Photo("s1", Apple, items);

    private static GroundedPhoto VisionRead(string[] printed, params GroundedItem[] items) =>
        Photo("s1", Vision, items) with { OcrLines = [.. printed.Select((text, i) => new GroundedOcrLine(text, 0.05 * i, 0.1, 0.04, 0.02))] };

    private static GroundedItem Backed() => Matched("Star Fox 64").ReadKind(WorkKind.Game).At(0.10);

    private static GroundedItem MadeUp() => Matched("The Matrix").ReadKind(WorkKind.Film);

    [Fact]
    public void A_model_read_the_shelfs_print_does_not_back_does_not_decide_the_shot_kind()
    {
        var kinds = EvalCombination.StandingModelKinds(
            VisionRead(["STAR FOX 64", "NINTENDO"], Matched("Star Fox 64").At(0.10)),
            ModelRead(Backed(), MadeUp()));

        kinds.ShouldBe([WorkKind.Game]);
    }

    [Fact]
    public void A_looping_models_repeated_reads_the_print_does_not_back_cannot_flip_the_shelfs_kind()
    {
        var loop = Enumerable.Repeat(MadeUp(), 40);

        var kinds = EvalCombination.StandingModelKinds(
            VisionRead(["STAR FOX 64", "NINTENDO"], Matched("Star Fox 64").At(0.10)),
            ModelRead([Backed(), .. loop]));

        kinds.ShouldBe([WorkKind.Game]);
    }

    [Fact]
    public void A_model_read_at_a_Vision_reads_place_stands_whatever_the_print_says()
    {
        var kinds = EvalCombination.StandingModelKinds(
            VisionRead(["NINTENDO"], Matched("Star Fox 64").At(0.10)),
            ModelRead(MadeUp().At(0.10)));

        kinds.ShouldBe([WorkKind.Film]);
    }

    [Fact]
    public void With_no_print_to_check_against_every_model_read_stands()
    {
        var kinds = EvalCombination.StandingModelKinds(
            VisionRead([], Matched("Star Fox 64").At(0.10)),
            ModelRead(Backed(), MadeUp()));

        kinds.ShouldBe([WorkKind.Game, WorkKind.Film]);
    }
}
