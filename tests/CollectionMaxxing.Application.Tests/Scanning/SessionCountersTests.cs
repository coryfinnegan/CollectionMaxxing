using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>The session counters reconcile: every candidate lands in exactly one bucket (docs/16 §5).</summary>
public class SessionCountersTests
{
    private static ScanCandidate Candidate(
        CandidateResolution resolution = CandidateResolution.Matched,
        CandidateDecision decision = CandidateDecision.Undecided,
        int alreadyOwnedCount = 0,
        Guid? createdItemId = null,
        Guid? snapId = null) => new()
        {
            Id = Guid.NewGuid(),
            LibraryId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Seq = 1,
            Source = CandidateSource.Barcode,
            Resolution = resolution,
            Decision = decision,
            AlreadyOwnedCount = alreadyOwnedCount,
            CreatedItemId = createdItemId,
            SnapId = snapId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void An_empty_session_counts_zero_everywhere()
    {
        var counters = SessionCounters.Count([]);

        counters.ShouldBe(new SessionCountersDto(Captured: 0, Added: 0, ToReview: 0, Owned: 0, Skipped: 0));
    }

    [Fact]
    public void A_candidate_with_an_item_is_added()
    {
        var counters = SessionCounters.Count([Candidate(createdItemId: Guid.NewGuid())]);

        counters.Added.ShouldBe(1);
        counters.Captured.ShouldBe(1);
    }

    [Fact]
    public void An_exact_owned_hit_is_owned_even_when_it_was_rejected()
    {
        // Reject-and-drop on an owned card is "Skip", never a failure (docs/16 §7).
        var counters = SessionCounters.Count([Candidate(decision: CandidateDecision.Rejected, alreadyOwnedCount: 1)]);

        counters.Owned.ShouldBe(1);
        counters.Skipped.ShouldBe(0);
    }

    [Fact]
    public void A_rejected_candidate_with_nothing_owned_is_skipped()
    {
        var counters = SessionCounters.Count([Candidate(decision: CandidateDecision.Rejected)]);

        counters.Skipped.ShouldBe(1);
        counters.ToReview.ShouldBe(0);
    }

    [Theory]
    [InlineData(CandidateResolution.Ambiguous)]
    [InlineData(CandidateResolution.NotFound)]
    [InlineData(CandidateResolution.Error)]
    [InlineData(CandidateResolution.Pending)]
    [InlineData(CandidateResolution.Matched)]
    public void Anything_still_open_needs_review(CandidateResolution resolution)
    {
        var counters = SessionCounters.Count([Candidate(resolution: resolution)]);

        counters.ToReview.ShouldBe(1);
    }

    [Fact]
    public void An_accepted_but_uncommitted_candidate_still_needs_review()
    {
        var counters = SessionCounters.Count([Candidate(decision: CandidateDecision.Accepted)]);

        counters.ToReview.ShouldBe(1);
        counters.Added.ShouldBe(0);
    }

    [Fact]
    public void Counters_reconcile_captured_is_added_plus_to_review_plus_owned_plus_skipped()
    {
        var candidates = new[]
        {
            Candidate(createdItemId: Guid.NewGuid()),
            Candidate(createdItemId: Guid.NewGuid()),
            Candidate(resolution: CandidateResolution.Ambiguous),
            Candidate(resolution: CandidateResolution.NotFound),
            Candidate(resolution: CandidateResolution.Pending),
            Candidate(alreadyOwnedCount: 2),
            Candidate(decision: CandidateDecision.Rejected),
        };

        var counters = SessionCounters.Count(candidates);

        counters.Captured.ShouldBe(7);
        (counters.Added + counters.ToReview + counters.Owned + counters.Skipped).ShouldBe(counters.Captured);
    }

    [Fact]
    public void Open_provisionals_count_as_captured_and_to_review()
    {
        var counters = SessionCounters.Count([Candidate(decision: CandidateDecision.Rejected)], openProvisionals: 2);

        counters.Captured.ShouldBe(3);
        counters.ToReview.ShouldBe(2);
        counters.Skipped.ShouldBe(1);
        (counters.Added + counters.ToReview + counters.Owned + counters.Skipped).ShouldBe(counters.Captured);
    }

    [Fact]
    public void Segments_follow_the_snaps_in_shoot_order_with_their_candidates()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var snaps = new[]
        {
            new ScanSnap { Id = second, LibraryId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Trigger = SnapTrigger.Shutter, Engine = ScanEngine.AppleModel, ItemCount = 1, CreatedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 1, TimeSpan.Zero) },
            new ScanSnap { Id = first, LibraryId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Trigger = SnapTrigger.Auto, Engine = ScanEngine.AppleModel, ItemCount = 2, CreatedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero) },
        };
        var one = Candidate(snapId: first);
        var two = Candidate(snapId: first);
        var three = Candidate(snapId: second);

        var segments = SessionCounters.Segments(snaps, [one, two, three]);

        segments.Count.ShouldBe(2);
        segments[0].SnapId.ShouldBe(first);
        segments[0].ItemCount.ShouldBe(2);
        segments[0].CandidateIds.ShouldBe([one.Id, two.Id]);
        segments[1].SnapId.ShouldBe(second);
    }

    [Fact]
    public void A_segment_is_resolving_while_any_candidate_is_pending()
    {
        var snapId = Guid.NewGuid();
        var snap = new ScanSnap { Id = snapId, LibraryId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Trigger = SnapTrigger.Auto, Engine = ScanEngine.AppleModel, ItemCount = 2, CreatedAt = DateTimeOffset.UtcNow };

        var segments = SessionCounters.Segments(
            [snap],
            [Candidate(resolution: CandidateResolution.Matched, snapId: snapId), Candidate(resolution: CandidateResolution.Pending, snapId: snapId)]);

        segments[0].State.ShouldBe(SessionSegmentState.Resolving);
    }

    [Theory]
    [InlineData(CandidateResolution.Ambiguous)]
    [InlineData(CandidateResolution.NotFound)]
    [InlineData(CandidateResolution.Error)]
    public void A_settled_segment_with_an_open_exception_needs_eyes(CandidateResolution resolution)
    {
        var snapId = Guid.NewGuid();
        var snap = new ScanSnap { Id = snapId, LibraryId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Trigger = SnapTrigger.Auto, Engine = ScanEngine.AppleModel, ItemCount = 1, CreatedAt = DateTimeOffset.UtcNow };

        var segments = SessionCounters.Segments([snap], [Candidate(resolution: resolution, snapId: snapId)]);

        segments[0].State.ShouldBe(SessionSegmentState.NeedsEye);
    }

    [Fact]
    public void A_segment_is_resolved_when_everything_matched_and_nothing_needs_a_decision()
    {
        var snapId = Guid.NewGuid();
        var snap = new ScanSnap { Id = snapId, LibraryId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Trigger = SnapTrigger.Auto, Engine = ScanEngine.AppleModel, ItemCount = 1, CreatedAt = DateTimeOffset.UtcNow };

        var segments = SessionCounters.Segments([snap], [Candidate(resolution: CandidateResolution.Matched, snapId: snapId)]);

        segments[0].State.ShouldBe(SessionSegmentState.Resolved);
    }

    [Fact]
    public void A_segment_is_reviewed_once_every_candidate_is_decided()
    {
        var snapId = Guid.NewGuid();
        var snap = new ScanSnap { Id = snapId, LibraryId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Trigger = SnapTrigger.Auto, Engine = ScanEngine.AppleModel, ItemCount = 2, CreatedAt = DateTimeOffset.UtcNow };

        var segments = SessionCounters.Segments(
            [snap],
            [Candidate(resolution: CandidateResolution.Matched, decision: CandidateDecision.Accepted, snapId: snapId),
             Candidate(resolution: CandidateResolution.NotFound, decision: CandidateDecision.Rejected, snapId: snapId)]);

        segments[0].State.ShouldBe(SessionSegmentState.Reviewed);
    }
}
