using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The regression rule allows for run-to-run noise (docs/16 §17): a rate on a row built on the on-device model may move as far as its sample size makes chance move it (the Vision and combined rows keep 2 points),
/// a time may grow by a ratio, and a raw count is not judged. The real numbers are the two clean runs on the owner's PC, 2026-10-31
/// (the baseline) and 2026-11-01 (the same build, nothing that affects scanning changed): the old rule called 23 of 255 metrics
/// regressed, and every one of them was noise.
/// </summary>
public sealed class NoiseAwareRegressionTests : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    /// <summary>
    /// The 23 metrics the plain 2-point rule flagged on 2026-11-01: the baseline value, the value that run measured, and the number of items
    /// the rate is a share of in that run (null for a count or a time, which are not shares).
    /// </summary>
    private static readonly (string Metric, double Baseline, double Actual, int? Items)[] FlaggedByThePlainRule =
    [
        ("apple_model.shelf.auto_add_precision", 40.91, 23.08, 13),
        ("apple_model.shelf.kind.film.top1", 14.85, 10.89, 101),
        ("apple_model.shelf.kind.film.top3", 14.85, 10.89, 101),
        ("apple_model.shelf.precision", 27.27, 24.12, 170),
        ("apple_model.shelf.provisional", 9, 25, null),
        ("apple_model.shelf.shotkind.shelf_segment.top1", 13.48, 10.64, 141),
        ("apple_model.shelf.shotkind.shelf_segment.top3", 14.89, 12.77, 141),
        ("apple_model.shelf.t1_auto", 22, 13, null),
        ("apple_model.shelf.total_time_to_trustworthy", 344.513, 455.798, null),
        ("apple_model.shelf.type_accuracy", 91.84, 89.36, 47),
        ("apple_model.single.total_time_to_trustworthy", 76.455, 82.716, null),
        ("apple_vision.shelf.provisional", 339, 349, null),
        ("apple_vision.shelf.total_time_to_trustworthy", 344.92, 456.232, null),
        ("apple_vision.single.total_time_to_trustworthy", 76.455, 82.716, null),
        ("fm_text.shelf.kind.book.top1", 8.16, 6.12, 245),
        ("fm_text.shelf.kind.book.top3", 8.57, 6.53, 245),
        ("fm_text.shelf.shotkind.unknown.top1", 6.45, 4.15, 217),
        ("fm_text.shelf.shotkind.unknown.top3", 7.37, 4.61, 217),
        ("fm_text.shelf.total_time_to_trustworthy", 238.041, 399.449, null),
        ("fm_text.single.auto_add_precision", 100, 66.67, 3),
        ("fm_text.single.total_time_to_trustworthy", 58.389, 68.979, null),
        ("vision_pipeline.shelf.total_time_to_trustworthy", 332.154, 444.784, null),
        ("vision_pipeline.single.total_time_to_trustworthy", 57.103, 59.974, null),
    ];

    private static Dictionary<string, double> Of(string metric, double value) => new() { [metric] = value };

    private static Dictionary<string, int> Items(string metric, int items) => new() { [metric] = items };

    // ---- the noise is gone, a real drop is not ---------------------------------------------------------------------------

    [Fact]
    public void The_23_metrics_the_plain_rule_flagged_between_two_runs_of_the_same_build_are_not_regressions()
    {
        var baseline = FlaggedByThePlainRule.ToDictionary(m => m.Metric, m => m.Baseline);
        var actual = FlaggedByThePlainRule.ToDictionary(m => m.Metric, m => m.Actual);
        var items = FlaggedByThePlainRule.Where(m => m.Items is not null).ToDictionary(m => m.Metric, m => m.Items!.Value);

        var check = EvalRegression.Check(baseline, actual, denominators: items);

        check.Regressions.Select(r => r.Metric).ShouldBeEmpty();
        check.Status.ShouldBe(EvalRegression.Ok);
        check.Compared.ShouldBe(20); // the 23 less the three raw counts (provisional twice, t1_auto once), which are not judged
    }

    [Fact]
    public void Without_the_sample_sizes_the_same_rates_are_judged_by_the_plain_2_point_rule_and_flagged()
    {
        var baseline = FlaggedByThePlainRule.ToDictionary(m => m.Metric, m => m.Baseline);
        var actual = FlaggedByThePlainRule.ToDictionary(m => m.Metric, m => m.Actual);

        var check = EvalRegression.Check(baseline, actual);

        check.Regressions.Select(r => r.Metric).ShouldContain("apple_model.shelf.auto_add_precision");
        check.Regressions.Select(r => r.Metric).ShouldContain("fm_text.shelf.kind.book.top1");
        check.Regressions.Select(r => r.Metric).ShouldNotContain("apple_model.shelf.t1_auto"); // counts and times are never judged by points
    }

    [Fact]
    public void A_6_point_drop_in_a_model_rows_shelf_recall_over_358_items_is_a_regression_and_says_what_was_allowed()
    {
        var check = EvalRegression.Check(
            Of("apple_model.shelf.recall", 38.0),
            Of("apple_model.shelf.recall", 32.0),
            denominators: Items("apple_model.shelf.recall", 358));

        check.Status.ShouldBe(EvalRegression.Regressed);
        var item = check.Regressions.Single();
        item.Metric.ShouldBe("apple_model.shelf.recall");
        item.Points.ShouldBe(6.0);
        item.Allowed.ShouldBe(5.13, 0.005); // 2 x 100 x sqrt(0.38 x 0.62 / 358)
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(358, true)]
    public void A_20_point_fall_in_a_rate_is_chance_over_12_items_and_a_regression_over_358(int items, bool flagged)
    {
        var check = EvalRegression.Check(
            Of("apple_model.shelf.top1", 60),
            Of("apple_model.shelf.top1", 40),
            denominators: Items("apple_model.shelf.top1", items));

        check.Regressions.Count.ShouldBe(flagged ? 1 : 0);
        check.Status.ShouldBe(flagged ? EvalRegression.Regressed : EvalRegression.Ok);
    }

    [Fact]
    public void The_allowance_is_never_below_2_points_however_large_the_sample()
    {
        var within = EvalRegression.Check(
            Of("apple_model.shelf.recall", 38),
            Of("apple_model.shelf.recall", 36.1),
            denominators: Items("apple_model.shelf.recall", 1_000_000));
        var past = EvalRegression.Check(
            Of("apple_model.shelf.recall", 38),
            Of("apple_model.shelf.recall", 35.5),
            denominators: Items("apple_model.shelf.recall", 1_000_000));

        within.Regressions.ShouldBeEmpty(); // 1.9 points worse
        past.Regressions.Single().Allowed.ShouldBe(2.0); // 2.5 points worse
    }

    [Fact]
    public void A_rate_that_improves_is_never_flagged_whatever_the_sample()
    {
        var check = EvalRegression.Check(
            Of("apple_model.shelf.top1", 40),
            Of("apple_model.shelf.top1", 90),
            denominators: Items("apple_model.shelf.top1", 5));

        check.Regressions.ShouldBeEmpty();
    }

    [Fact]
    public void A_rate_with_no_sample_size_keeps_the_plain_2_point_rule()
    {
        var baseline = Of("apple_model.shelf.top1", 50);
        var actual = Of("apple_model.shelf.top1", 47);

        foreach (var denominators in new IReadOnlyDictionary<string, int>?[] { null, new Dictionary<string, int>(), Items("apple_model.shelf.top1", 0), Items("apple_model.shelf.recall", 358) })
        {
            var item = EvalRegression.Check(baseline, actual, denominators: denominators).Regressions.Single();

            item.Points.ShouldBe(3.0);
            item.Allowed.ShouldBe(2.0);
        }
    }

    [Fact]
    public void A_rate_whose_baseline_is_exactly_100_is_judged_at_a_smoothed_boundary_not_at_no_spread()
    {
        // 100 over a few auto-adds says little: 100 -> 66.67 over three is chance, 100 -> 50 over four is not, and over 400 even 90 is not.
        var three = EvalRegression.Check(
            Of("fm_text.single.auto_add_precision", 100),
            Of("fm_text.single.auto_add_precision", 66.67),
            denominators: Items("fm_text.single.auto_add_precision", 3));
        var four = EvalRegression.Check(
            Of("apple_model.single.auto_add_precision", 100),
            Of("apple_model.single.auto_add_precision", 50),
            denominators: Items("apple_model.single.auto_add_precision", 4));
        var large = EvalRegression.Check(
            Of("fm_text.single.auto_add_precision", 100),
            Of("fm_text.single.auto_add_precision", 90),
            denominators: Items("fm_text.single.auto_add_precision", 400));

        three.Regressions.ShouldBeEmpty(); // allowed 46.19: 2 x 100 x sqrt(0.8 x 0.2 / 3), with p = (n + 1) / (n + 2)
        four.Regressions.Single().Allowed.ShouldBe(37.27, 0.005); // 2 x 100 x sqrt((5/6) x (1/6) / 4)
        large.Regressions.Single().Allowed.ShouldBe(2.0); // the smoothed spread over 400 is below the floor
    }

    [Fact]
    public void A_rate_whose_baseline_is_exactly_0_is_judged_at_a_smoothed_boundary_too()
    {
        // p = 1 / (n + 2): 0 -> 18.18 over eleven items read is flagged, one invented in ten is chance.
        var eleven = EvalRegression.Check(
            Of("apple_model.single.invented_pct", 0),
            Of("apple_model.single.invented_pct", 18.18),
            denominators: Items("apple_model.single.invented_pct", 11));
        var ten = EvalRegression.Check(
            Of("apple_model.single.invented_pct", 0),
            Of("apple_model.single.invented_pct", 10),
            denominators: Items("apple_model.single.invented_pct", 10));

        eleven.Regressions.Single().Allowed.ShouldBe(16.07, 0.005); // 2 x 100 x sqrt((1/13) x (12/13) / 11)
        ten.Regressions.ShouldBeEmpty(); // allowed 17.48
    }

    [Fact]
    public void A_rate_that_is_0_or_100_in_both_runs_is_never_flagged()
    {
        var check = EvalRegression.Check(
            Of("apple_model.shelf.top1", 100),
            Of("apple_model.shelf.top1", 100),
            denominators: Items("apple_model.shelf.top1", 12));
        var worse = EvalRegression.Check(
            Of("apple_model.shelf.invented_pct", 0),
            Of("apple_model.shelf.invented_pct", 0),
            denominators: Items("apple_model.shelf.invented_pct", 12));

        check.Regressions.ShouldBeEmpty();
        worse.Regressions.ShouldBeEmpty();
    }

    // ---- only the rows that answer differently each run get the sample-size allowance -------------------------------------

    [Theory]
    [InlineData("apple_model")]
    [InlineData("fm_text")]
    public void The_rows_built_on_the_on_device_model_keep_the_sample_size_allowance(string engine)
    {
        var check = EvalRegression.Check(
            Of($"{engine}.shelf.top1", 60),
            Of($"{engine}.shelf.top1", 40),
            denominators: Items($"{engine}.shelf.top1", 12));

        check.Regressions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("apple_vision")]
    [InlineData("vision_pipeline")]
    public void The_deterministic_and_combined_rows_keep_the_plain_2_points_whatever_the_sample(string engine)
    {
        var check = EvalRegression.Check(
            Of($"{engine}.shelf.top1", 60),
            Of($"{engine}.shelf.top1", 40),
            denominators: Items($"{engine}.shelf.top1", 12));

        var item = check.Regressions.Single();
        item.Points.ShouldBe(20.0);
        item.Allowed.ShouldBe(2.0);
    }

    [Fact]
    public void A_25_point_fall_in_the_combined_rows_single_top1_over_12_items_is_a_regression()
    {
        var check = EvalRegression.Check(
            Of("apple_vision.single.top1", 75),
            Of("apple_vision.single.top1", 50),
            denominators: Items("apple_vision.single.top1", 12));

        check.Regressions.Single().Allowed.ShouldBe(2.0);
    }

    [Fact]
    public void A_5_point_fall_in_the_combined_rows_shelf_recall_over_358_items_is_a_regression_and_so_is_a_vision_precision_fall()
    {
        var recall = EvalRegression.Check(
            Of("apple_vision.shelf.recall", 38),
            Of("apple_vision.shelf.recall", 33),
            denominators: Items("apple_vision.shelf.recall", 358));
        var precision = EvalRegression.Check(
            Of("apple_vision.single.precision", 81.82),
            Of("apple_vision.single.precision", 63.64),
            denominators: Items("apple_vision.single.precision", 11));
        var vision = EvalRegression.Check(
            Of("vision_pipeline.shelf.recall", 36.31),
            Of("vision_pipeline.shelf.recall", 33),
            denominators: Items("vision_pipeline.shelf.recall", 358));

        recall.Regressions.Single().Allowed.ShouldBe(2.0);
        precision.Regressions.Single().Allowed.ShouldBe(2.0);
        vision.Regressions.Single().Allowed.ShouldBe(2.0);
    }

    [Fact]
    public void A_row_the_rule_does_not_name_keeps_the_plain_2_points()
    {
        var check = EvalRegression.Check(Of("a.top1", 60), Of("a.top1", 40), denominators: Items("a.top1", 12));

        check.Regressions.Single().Allowed.ShouldBe(2.0);
    }

    [Theory]
    [InlineData(12.0, true)]
    [InlineData(4.0, false)]
    public void For_invented_titles_a_rise_past_the_allowance_is_the_regression(double now, bool flagged)
    {
        var check = EvalRegression.Check(
            Of("apple_model.shelf.invented_pct", 2),
            Of("apple_model.shelf.invented_pct", now),
            denominators: Items("apple_model.shelf.invented_pct", 100));

        check.Regressions.Count.ShouldBe(flagged ? 1 : 0);
        if (flagged)
        {
            check.Regressions.Single().Points.ShouldBe(10.0);
            check.Regressions.Single().Allowed.ShouldBe(2.8, 0.005); // 2 x 100 x sqrt(0.02 x 0.98 / 100)
        }
    }

    [Fact]
    public void Refusals_and_timeouts_are_shares_of_the_photos_and_review_rate_of_the_truth_items()
    {
        var baseline = new Dictionary<string, double>
        {
            ["apple_model.shelf.refusal_pct"] = 10,
            ["apple_model.shelf.timeout_pct"] = 10,
            ["apple_model.shelf.review_rate"] = 50,
        };
        var actual = new Dictionary<string, double>
        {
            ["apple_model.shelf.refusal_pct"] = 20,
            ["apple_model.shelf.timeout_pct"] = 20,
            ["apple_model.shelf.review_rate"] = 70,
        };
        var items = new Dictionary<string, int>
        {
            ["apple_model.shelf.refusal_pct"] = 10, // one more refusal in ten photos
            ["apple_model.shelf.timeout_pct"] = 400,
            ["apple_model.shelf.review_rate"] = 358,
        };

        var check = EvalRegression.Check(baseline, actual, denominators: items);

        check.Regressions.Select(r => r.Metric).ShouldBe(["apple_model.shelf.review_rate", "apple_model.shelf.timeout_pct"]);
    }

    // ---- times: a ratio ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_time_is_a_regression_only_past_the_allowed_ratio_of_the_baseline()
    {
        var baseline = Of("apple_model.shelf.total_time_to_trustworthy", 300);

        EvalRegression.Check(baseline, Of("apple_model.shelf.total_time_to_trustworthy", 390)).Regressions.ShouldBeEmpty(); // 1.3 x
        EvalRegression.Check(baseline, Of("apple_model.shelf.total_time_to_trustworthy", 100)).Regressions.ShouldBeEmpty(); // faster

        var check = EvalRegression.Check(baseline, Of("apple_model.shelf.total_time_to_trustworthy", 750)); // 2.5 x
        check.Status.ShouldBe(EvalRegression.Regressed);
        var item = check.Regressions.Single();
        item.Points.ShouldBe(450); // seconds slower
        item.Allowed.ShouldBe(300 * (EvalRegression.MaxTimeRatio - 1));
    }

    [Fact]
    public void The_time_to_first_results_is_judged_the_same_way_whatever_the_sample_sizes()
    {
        var baseline = Of("apple_vision.shelf.time_to_first_results", 11.448);

        EvalRegression.Check(baseline, Of("apple_vision.shelf.time_to_first_results", 14.9), denominators: Items("apple_vision.shelf.time_to_first_results", 1)).Regressions.ShouldBeEmpty();
        EvalRegression.Check(baseline, Of("apple_vision.shelf.time_to_first_results", 30)).Regressions.Single().Metric.ShouldBe("apple_vision.shelf.time_to_first_results");
    }

    [Fact]
    public void The_slowest_the_same_build_ran_between_the_two_real_runs_is_not_a_regression()
    {
        // fm_text, shelf: 238.041 s on 2026-10-31 and 399.449 s on 2026-11-01, the same build: 1.68 x.
        var check = EvalRegression.Check(
            Of("fm_text.shelf.total_time_to_trustworthy", 238.041),
            Of("fm_text.shelf.total_time_to_trustworthy", 399.449));

        check.Regressions.ShouldBeEmpty();
    }

    // ---- counts are not judged --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("t0_auto")]
    [InlineData("t1_auto")]
    [InlineData("t2_review")]
    [InlineData("provisional")]
    [InlineData("unreconciled_photos")]
    [InlineData("excess_unreadable")]
    public void A_raw_count_is_never_a_regression_and_is_not_counted_as_compared(string count)
    {
        var baseline = new Dictionary<string, double> { [$"apple_model.shelf.{count}"] = 1, ["apple_model.shelf.top1"] = 50 };
        var actual = new Dictionary<string, double> { [$"apple_model.shelf.{count}"] = 500, ["apple_model.shelf.top1"] = 50 };

        var check = EvalRegression.Check(baseline, actual);

        check.Status.ShouldBe(EvalRegression.Ok);
        check.Compared.ShouldBe(1); // only the rate was judged
        EvalRegression.IsJudged($"apple_model.shelf.{count}").ShouldBeFalse();
        EvalRegression.IsJudged("apple_model.shelf.top1").ShouldBeTrue();
    }

    [Fact]
    public void A_baseline_that_shares_only_raw_counts_with_the_run_compared_nothing_so_it_is_no_overlap()
    {
        var check = EvalRegression.Check(Of("apple_model.shelf.t1_auto", 22), Of("apple_model.shelf.t1_auto", 13));

        check.Status.ShouldBe(EvalRegression.NoOverlap);
        check.Compared.ShouldBe(0);
    }

    // ---- the duplicate check keeps the 2 points ----------------------------------------------------------------------------

    [Fact]
    public void A_duplicate_check_that_flips_from_clean_to_dirty_is_flagged_by_the_2_point_rule_even_with_sample_sizes_around()
    {
        var check = EvalRegression.Check(
            Of("apple_model.shelf.duplicate_clean", 100),
            Of("apple_model.shelf.duplicate_clean", 0),
            denominators: Items("apple_model.shelf.duplicate_clean", 1));

        var item = check.Regressions.Single();
        item.Points.ShouldBe(100);
        item.Allowed.ShouldBe(2.0);
    }

    // ---- the sample sizes come from the scored run -------------------------------------------------------------------------

    /// <summary>
    /// Two shelf pictures (the second one refused) and two single pictures (the second one refused), scored for the Apple model: truth 5 and 1
    /// on the shelves, 2 unreadable segments beyond what the truth allows, 3 items read of which 2 are right (one auto-added), and so on.
    /// </summary>
    private ScoredEngines ScoreTheScenario()
    {
        _golden.AddShelf(
            "s1",
            [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64"), ShelfItem("Banjo-Kazooie"), ShelfItem("Goldeneye 007"), ShelfItem("The Hobbit", mediaType: "book", platform: null)],
            shotKind: "shelf_segment");
        _golden.AddShelf("s2", [ShelfItem("Perfect Dark")]);
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "complete", EvalKinds.LooseCartridge, "Nintendo 64"));
        _golden.AddSingle("c2", new SyntheticGolden.SingleItem("Mario Kart 64", "game_cartridge", "complete", EvalKinds.LooseCartridge, "Nintendo 64"));
        return EvalScorer.Score(
            [
                Photo("s1", Apple, [T1(Matched("Star Fox 64")), T2(Matched("Mario Kart 64")), NotFound("smudged spine")], unreadable: 2),
                Photo("s2", Apple, [], outcome: "refused"),
                Photo("c1", Apple, [T1(Matched("Star Fox 64", completeness: "complete"))]),
                Photo("c2", Apple, [], outcome: "refused"),
            ],
            TruthsOf(_golden));
    }

    [Fact]
    public void Each_rate_is_a_share_of_the_items_the_scorer_divides_by()
    {
        var items = EvalRegression.Denominators(ScoreTheScenario().Engines);

        // shelf: 6 truth items (5 + 1) and 2 unreadable beyond the truth's 0, 3 items read, 2 of them matched to the truth, 1 auto-add, 2 pictures
        foreach (var rate in new[] { "recall", "top1", "top3", "title_accuracy" })
        {
            items[$"apple_model.shelf.{rate}"].ShouldBe(8);
        }

        items["apple_model.shelf.precision"].ShouldBe(3);
        items["apple_model.shelf.invented_pct"].ShouldBe(3);
        items["apple_model.shelf.type_accuracy"].ShouldBe(2); // over the truth items whose title was found
        items["apple_model.shelf.platform_accuracy"].ShouldBe(2); // the two found items whose truth names a platform
        items["apple_model.shelf.review_rate"].ShouldBe(6);
        items["apple_model.shelf.auto_add_precision"].ShouldBe(1);
        items["apple_model.shelf.refusal_pct"].ShouldBe(2);
        items["apple_model.shelf.timeout_pct"].ShouldBe(2);
        items["apple_model.shelf.kind.game.top1"].ShouldBe(5);
        items["apple_model.shelf.kind.game.top3"].ShouldBe(5);
        items["apple_model.shelf.kind.book.top1"].ShouldBe(1);
        items["apple_model.shelf.shotkind.shelf_segment.top1"].ShouldBe(5);
        items["apple_model.shelf.shotkind.shelf_segment.top3"].ShouldBe(5);
        items["apple_model.shelf.shotkind.unknown.top1"].ShouldBe(1);
        items.ContainsKey("apple_model.shelf.completeness_accuracy").ShouldBeFalse(); // only a single picture has a completeness to check

        // single: 2 pictures, 1 read, one right, the completeness checked once
        items["apple_model.single.recall"].ShouldBe(2);
        items["apple_model.single.precision"].ShouldBe(1);
        items["apple_model.single.completeness_accuracy"].ShouldBe(1);
        items["apple_model.single.auto_add_precision"].ShouldBe(1);
        items["apple_model.single.refusal_pct"].ShouldBe(2);
    }

    [Fact]
    public void Every_rate_the_flattening_names_has_a_sample_size_and_its_share_of_that_size_is_a_whole_number_of_items()
    {
        var engines = ScoreTheScenario().Engines;
        var metrics = EvalRegression.Flatten(engines);
        var items = EvalRegression.Denominators(engines);

        var rates = metrics.Where(m => EvalRegression.IsRate(m.Key)).ToList();
        rates.ShouldNotBeEmpty();
        foreach (var (name, percent) in rates)
        {
            items.ContainsKey(name).ShouldBeTrue($"{name} has no sample size");
            var hits = percent * items[name] / 100.0;
            Math.Abs(hits - Math.Round(hits)).ShouldBeLessThan(0.01, $"{name} = {percent}% is not a whole number of {items[name]} items");
        }
    }

    [Fact]
    public void The_sample_sizes_have_the_same_names_as_the_flattened_metrics()
    {
        var engines = ScoreTheScenario().Engines;

        EvalRegression.Denominators(engines).Keys.Except(EvalRegression.Flatten(engines).Keys).ShouldBeEmpty();
    }

    [Fact]
    public void A_scored_run_passes_its_own_sample_sizes_to_the_check()
    {
        // 120 truth items, 60 read right: 50% recall against a baseline of 70. The same fall over 12 items is chance.
        var big = Compare(120, 60, baselineRecall: 70);
        var small = Compare(12, 6, baselineRecall: 70);

        big.Regression.Status.ShouldBe(EvalRegression.Regressed);
        big.Regression.Regressions.Select(r => r.Metric).ShouldContain("apple_model.shelf.recall");
        small.Regression.Regressions.Select(r => r.Metric).ShouldNotContain("apple_model.shelf.recall");
    }

    private EvalSummary Compare(int truthItems, int right, double baselineRecall)
    {
        var titles = Enumerable.Range(1, truthItems).Select(i => $"Test Title {(char)('A' + (i / 26))}{(char)('A' + (i % 26))}").ToList();
        _golden.AddShelf("s1", titles.Select(t => ShelfItem(t)));
        var read = titles.Take(right).Select(t => T2(Matched(t))).ToList();
        var baseline = Of("apple_model.shelf.recall", baselineRecall);
        return EvalReport.Evaluate(RunOf(Photo("s1", Apple, read)), TruthsOf(_golden), baseline, "abc1234", []).Summary;
    }

    // ---- the report ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_report_shows_what_was_allowed_next_to_each_flagged_metric()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")]);
        var run = RunOf(Photo("s1", Apple, []));

        var report = EvalReport.Evaluate(run, TruthsOf(_golden), Of("apple_model.shelf.recall", 100), "abc1234", []).ReportMarkdown;

        report.ShouldContain("apple_model.shelf.recall: 100 -> 0 (dropped 100, allowed 61.24)"); // two truth items: 2 x 100 x sqrt(0.75 x 0.25 / 2)
    }

    [Fact]
    public void A_time_in_the_report_is_in_seconds_slower_against_the_seconds_allowed()
    {
        var item = new RegressionItem("apple_model.shelf.total_time_to_trustworthy", 300, 750, 450, 300);

        EvalRegression.Describe(item).ShouldBe("450 s slower, allowed 300 s");
        EvalRegression.Describe(new RegressionItem("apple_model.shelf.invented_pct", 2, 12, 10, 2.8)).ShouldBe("rose 10, allowed 2.8");
        EvalRegression.Describe(new RegressionItem("apple_model.shelf.top1", 38, 32, 6, 5.13)).ShouldBe("dropped 6, allowed 5.13");
    }

    [Fact]
    public void A_regression_item_from_a_result_file_written_before_the_allowance_was_recorded_reads_as_the_2_point_rule()
    {
        var item = System.Text.Json.JsonSerializer.Deserialize<RegressionItem>(
            """{ "metric": "apple_model.single.top1", "baseline": 90, "actual": 80, "points": 10 }""",
            WebJson);

        item.ShouldNotBeNull();
        item.Allowed.ShouldBe(2.0);
    }
}
