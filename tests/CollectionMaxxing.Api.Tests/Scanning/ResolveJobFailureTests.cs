using CollectionMaxxing.Api.Tests.Scanning.Identify;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Scanning;

// A resolve job that fails for good (its retries are used up) must not leave its row saying "Looking up…" until the 7-day
// clean-up: the row becomes an Error, the state the app already shows with a way out (search by hand, shoot the cover).
public class ResolveJobFailureTests : IdentifyTest
{
    private readonly Outage _outage = new();
    private int _maxAttempts = 1;

    public ResolveJobFailureTests(ApiFixture api)
        : base(api)
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        ConfigureServices = services =>
        {
            // A fault inside the job (not a provider that is down, which the job already turns into an Error itself).
            services.RemoveAll<IMembershipReader>();
            services.AddScoped<IMembershipReader>(provider =>
                new FlakyMemberships(ActivatorUtilities.CreateInstance<MembershipReader>(provider), _outage));
            services.Configure<JobOptions>(options => options.Retry = new JobRetryPolicy { MaxAttempts = _maxAttempts });
        };
    }

    private sealed class Outage
    {
        public bool Down { get; set; }
    }

    private sealed class FlakyMemberships(IMembershipReader inner, Outage outage) : IMembershipReader
    {
        public Task<IReadOnlyList<Membership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken) =>
            outage.Down ? throw new InvalidOperationException("the database went away") : inner.GetMembershipsAsync(userId, cancellationToken);
    }

    /// <summary>Runs every due job while the fault is on, then ends the fault so the test can look at the result through the API.</summary>
    private async Task RunJobsDuringOutageAsync()
    {
        _outage.Down = true;
        try
        {
            using var scope = Factory.Services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<JobRunner>();
            for (var i = 0; i < 10 && await runner.RunNextAsync("test-worker", CancellationToken.None); i++)
            {
            }
        }
        finally
        {
            _outage.Down = false;
        }
    }

    private async Task<ScanCandidate> CandidateRowAsync(Guid candidateId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == candidateId);
    }

    private async Task<List<JobStatus>> JobStatusesAsync()
    {
        await using var context = Db.CreateContext();
        return await context.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).Select(j => j.Status).ToListAsync();
    }

    private async Task<(HttpClient Editor, Guid SessionId, Guid CandidateId)> PendingBarcodeAsync(TestLibrary library, int seed)
    {
        var code = ScanData.Upc(seed);
        Products.Behaviour = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return ProductLookupResult.NotFound;
        };
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        Resolution(candidate).ShouldBe("pending");
        return (editor, sessionId, Id(candidate));
    }

    private async Task<(HttpClient Editor, Guid SessionId, Guid CandidateId)> PendingPictureAsync(TestLibrary library)
    {
        Games.Behaviour = ct => Task.Delay(Timeout.Infinite, ct);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(editor, sessionId, Read(title: "The Legend Of Zorblax Quest", kind: "game")))[0];
        Resolution(candidate).ShouldBe("pending");
        return (editor, sessionId, Id(candidate));
    }

    [Fact]
    public async Task A_pending_code_whose_resolve_job_failed_for_good_becomes_an_error_the_person_can_search_by_hand()
    {
        var library = await CreateLibraryAsync();
        var (editor, sessionId, candidateId) = await PendingBarcodeAsync(library, 6401);
        using var client = editor;

        await RunJobsDuringOutageAsync();

        (await JobStatusesAsync()).ShouldContain(JobStatus.Failed);
        var row = await CandidateRowAsync(candidateId);
        row.Resolution.ShouldBe(CandidateResolution.Error);
        row.Tier.ShouldBe(ScanTier.T2);
        row.GuidanceCode.ShouldBe(GuidanceCode.ShootCover);
        var session = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray().Single();
        Resolution(session).ShouldBe("error");
    }

    [Fact]
    public async Task A_pending_picture_whose_resolve_job_failed_for_good_becomes_an_error_the_person_can_fix_by_hand()
    {
        var library = await CreateLibraryAsync();
        var (editor, sessionId, candidateId) = await PendingPictureAsync(library);
        using var client = editor;

        await RunJobsDuringOutageAsync();

        var row = await CandidateRowAsync(candidateId);
        row.Resolution.ShouldBe(CandidateResolution.Error);
        row.Tier.ShouldBe(ScanTier.T2);
        Resolution((await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray().Single()).ShouldBe("error");
    }

    [Fact]
    public async Task A_resolve_job_that_will_be_retried_leaves_its_row_pending()
    {
        _maxAttempts = 3;
        var library = await CreateLibraryAsync();
        var (editor, _, candidateId) = await PendingBarcodeAsync(library, 6402);
        using var client = editor;

        await RunJobsDuringOutageAsync();

        (await JobStatusesAsync()).ShouldNotContain(JobStatus.Failed);
        (await CandidateRowAsync(candidateId)).Resolution.ShouldBe(CandidateResolution.Pending, "it still has attempts left");
    }

    [Fact]
    public async Task A_failure_for_good_does_not_touch_a_row_that_was_settled_meanwhile()
    {
        var library = await CreateLibraryAsync();
        var (editor, _, candidateId) = await PendingPictureAsync(library);
        using var client = editor;
        await using (var context = Db.CreateContext())
        {
            await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == candidateId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Resolution, CandidateResolution.NotFound));
        }

        await RunJobsDuringOutageAsync();

        (await CandidateRowAsync(candidateId)).Resolution.ShouldBe(CandidateResolution.NotFound);
    }
}
