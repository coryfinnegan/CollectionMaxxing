using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>What the baseline file gives the regression check: the metrics, and the threshold fingerprint they were measured under.</summary>
public sealed class BaselineDocTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-baseline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private FileEvalResults ResultsOf(string baselineJson)
    {
        Directory.CreateDirectory(_folder);
        var baselinePath = Path.Combine(_folder, "baseline.json");
        File.WriteAllText(baselinePath, baselineJson);
        return new FileEvalResults(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _folder, BaselinePath = baselinePath }));
    }

    [Fact]
    public void The_doc_carries_the_metrics_and_the_threshold_hash()
    {
        var doc = ResultsOf("""{ "note": "first real baseline", "thresholdHash": "00e2732db24a2394", "metrics": { "a.top1": 90 } }""").LoadBaselineDoc();

        doc.ShouldNotBeNull();
        doc.Metrics["a.top1"].ShouldBe(90);
        doc.ThresholdHash.ShouldBe("00e2732db24a2394");
        ResultsOf("""{ "metrics": { "a.top1": 90 } }""").LoadBaseline()!["a.top1"].ShouldBe(90);
    }

    [Fact]
    public void A_baseline_from_before_thresholds_has_metrics_but_no_hash()
    {
        var doc = ResultsOf("""{ "note": "old baseline", "metrics": { "a.top1": 90 } }""").LoadBaselineDoc();

        doc.ShouldNotBeNull();
        doc.Metrics["a.top1"].ShouldBe(90);
        doc.ThresholdHash.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{ "thresholdHash": 42, "metrics": { "a.top1": 90 } }""")]
    [InlineData("""{ "thresholdHash": "", "metrics": { "a.top1": 90 } }""")]
    public void A_hash_that_is_not_a_short_string_is_ignored_not_fatal(string json)
    {
        var doc = ResultsOf(json).LoadBaselineDoc();

        doc.ShouldNotBeNull();
        doc.Metrics["a.top1"].ShouldBe(90);
        doc.ThresholdHash.ShouldBeNull();
    }

    [Fact]
    public void A_partial_summary_is_still_refused_as_a_baseline_doc()
    {
        ResultsOf("""{ "partial": true, "thresholdHash": "00e2732db24a2394", "metrics": { "a.top1": 90 } }""")
            .LoadBaselineDoc().ShouldBeNull();
    }

    [Fact]
    public void A_missing_or_broken_baseline_is_no_doc()
    {
        new FileEvalResults(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _folder, BaselinePath = null }))
            .LoadBaselineDoc().ShouldBeNull();
        ResultsOf("not json").LoadBaselineDoc().ShouldBeNull();
        ResultsOf("""{ "note": "no metrics here" }""").LoadBaselineDoc().ShouldBeNull();
    }
}
