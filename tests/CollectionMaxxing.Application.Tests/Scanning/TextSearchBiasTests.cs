using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning;

public class TextSearchBiasTests
{
    private static readonly ScoringProfile Profile = new ScoringOptions().Profile(ScoringProfileKind.BarcodeProduct);

    private static readonly ReadItem Read = new() { Title = "Dune Messiah" };

    /// <summary>Four local releases that all read as "Dune Messiah" (a tie the scorer leaves in pool order), plus a near miss.</summary>
    private static (LocalPool Pool, Guid[] ReleaseIds, Guid[] WorkIds) FourWayTie()
    {
        var titles = new[] { "Dune Messiah", "Dune  Messiah", "dune messiah", "DUNE MESSIAH", "Dune Messiahs" };
        var items = new List<(CandidateRelease Scoring, ResolutionOption Option)>();
        var workIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var releaseIds = new List<Guid>();
        var works = new List<Guid>();
        foreach (var title in titles)
        {
            var releaseId = Guid.NewGuid();
            var workId = Guid.NewGuid();
            releaseIds.Add(releaseId);
            works.Add(workId);
            var key = releaseId.ToString();
            items.Add((new CandidateRelease { Key = key, Title = title }, new ResolutionOption(releaseId, null, 0)));
            workIds[key] = workId;
        }

        return (new LocalPool(items, null, workIds), [.. releaseIds], [.. works]);
    }

    private static List<Guid?> OptionIds(BarcodeResolution resolution) =>
        resolution.Options.Select(o => o.LocalReleaseId).ToList();

    [Fact]
    public void Without_context_a_close_call_keeps_pool_order()
    {
        var (pool, releaseIds, _) = FourWayTie();

        var resolution = TextSearchResolver.Score(Read, Profile, pool, ProviderAnswers.None, pendingWhenProvidersFailed: false);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        OptionIds(resolution).ShouldBe(releaseIds.Take(3).Select(id => (Guid?)id).ToList());
        resolution.Steps.Any(s => s.Provider == "context").ShouldBeFalse();
    }

    [Fact]
    public void Neighbour_context_lifts_a_close_call_but_changes_no_verdict_or_score()
    {
        var (pool, releaseIds, workIds) = FourWayTie();
        var plain = TextSearchResolver.Score(Read, Profile, pool, ProviderAnswers.None, pendingWhenProvidersFailed: false);

        var biased = TextSearchResolver.Score(
            Read, Profile, pool, ProviderAnswers.None, pendingWhenProvidersFailed: false, [workIds[2]], pool.WorkIdsByKey);

        biased.Verdict.ShouldBe(plain.Verdict);
        OptionIds(biased).ShouldBe([(Guid?)releaseIds[2], releaseIds[0], releaseIds[1]]);
        biased.Options.ShouldAllBe(o => o.Score == 1.0); // the bonus reorders; it is never persisted as a score
        biased.Steps.ShouldContain(s => s.Provider == "context" && s.Outcome == "biased");
    }

    [Fact]
    public void The_globally_best_match_stays_selectable_when_every_neighbour_outranks_it()
    {
        // The right answer is outside the neighbour set: bias may slide it down, never out of the top three.
        var (pool, releaseIds, workIds) = FourWayTie();

        var biased = TextSearchResolver.Score(
            Read, Profile, pool, ProviderAnswers.None, pendingWhenProvidersFailed: false,
            [workIds[1], workIds[2], workIds[3]], pool.WorkIdsByKey);

        OptionIds(biased).ShouldBe([(Guid?)releaseIds[1], releaseIds[2], releaseIds[0]]);
    }

    [Fact]
    public void Unknown_context_ids_change_nothing()
    {
        var (pool, releaseIds, _) = FourWayTie();

        var biased = TextSearchResolver.Score(
            Read, Profile, pool, ProviderAnswers.None, pendingWhenProvidersFailed: false, [Guid.NewGuid()], pool.WorkIdsByKey);

        biased.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        OptionIds(biased).ShouldBe(releaseIds.Take(3).Select(id => (Guid?)id).ToList());
        biased.Steps.Any(s => s.Provider == "context").ShouldBeFalse();
    }

    [Fact]
    public void Provider_releases_have_no_work_so_the_bias_passes_them_by()
    {
        var (pool, releaseIds, workIds) = FourWayTie();
        var providers = new ProviderAnswers(
            [new ProviderRelease { Provider = "test", ExternalId = "1", Title = "Dune Messiah!" }], [], false);

        var biased = TextSearchResolver.Score(
            Read, Profile, pool, providers, pendingWhenProvidersFailed: false, [workIds[1]], pool.WorkIdsByKey);

        // The provider's twin scores a perfect 1.0 too, but without a work it never jumps the biased local.
        OptionIds(biased).ShouldBe([(Guid?)releaseIds[1], releaseIds[0], releaseIds[2]]);
    }

    [Fact]
    public void A_matched_top_can_never_lose_its_lead_to_the_bias()
    {
        // Matched needs a lead of 0.08; the bias is 0.05 by design, so the top always survives it.
        var top = Guid.NewGuid();
        var runnerUp = Guid.NewGuid();
        var runnerWork = Guid.NewGuid();
        var pool = new LocalPool(
            [
                (new CandidateRelease { Key = top.ToString(), Title = "Dune Messiah", Creators = ["Frank Herbert"] }, new ResolutionOption(top, null, 0)),
                (new CandidateRelease { Key = runnerUp.ToString(), Title = "Dune", Creators = ["Isaac Asimov"] }, new ResolutionOption(runnerUp, null, 0)),
            ],
            null,
            new Dictionary<string, Guid> { [top.ToString()] = Guid.NewGuid(), [runnerUp.ToString()] = runnerWork });
        var read = new ReadItem { Title = "Dune Messiah", Creator = "Frank Herbert" };

        var biased = TextSearchResolver.Score(
            read, Profile, pool, ProviderAnswers.None, pendingWhenProvidersFailed: false, [runnerWork], pool.WorkIdsByKey);

        biased.Verdict.ShouldBe(ResolutionVerdict.Matched);
        OptionIds(biased).ShouldBe([(Guid?)top, runnerUp]);
    }
}
