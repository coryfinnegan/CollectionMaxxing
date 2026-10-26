using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Audit;
using CollectionMaxxing.Infrastructure.Ids;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Deletion;

/// <summary>Purging an account takes its kept rows and their crops: no provisional rows and no crop files are left (#345).</summary>
public class PurgeProvisionalTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset DeletedAt = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeAccounts : IDeletedAccounts
    {
        public Task<DateTimeOffset?> DeletedAtAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(DeletedAt);

        public Task ScrubAuditTrailAsync(Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EraseAsync(Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeFiles : IAccountFiles
    {
        public HashSet<string> Existing { get; } = [];

        public Task DeleteAsync(string blobPath, CancellationToken cancellationToken)
        {
            Existing.Remove(blobPath);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Purging_removes_kept_rows_and_their_crop_files()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.LibraryId == library.Id && m.Role != LibraryRole.Owner).ExecuteDeleteAsync();
        }

        var sessionId = await TestData.CreateScanSessionAsync(Db, library, library.OwnerId);
        var cropPath = "cr/op/crop.jpg";
        var files = new FakeFiles();
        files.Existing.Add(cropPath);
        await using (var context = Db.CreateContext())
        {
            var snapId = Guid.CreateVersion7();
            var candidateId = Guid.CreateVersion7();
            var assetId = Guid.CreateVersion7();
            context.ScanSnaps.Add(new ScanSnap { Id = snapId, LibraryId = library.Id, SessionId = sessionId, Trigger = SnapTrigger.Shutter, CreatedAt = DeletedAt });
            context.ScanCandidates.Add(new ScanCandidate { Id = candidateId, LibraryId = library.Id, SessionId = sessionId, SnapId = snapId });
            context.Assets.Add(new Asset
            {
                Id = assetId,
                Kind = AssetKind.ScanPhoto,
                BlobPath = cropPath,
                ContentType = "image/jpeg",
                Sha256 = assetId.ToString("N"),
                OwnerLibraryId = library.Id,
                CreatedAt = DeletedAt,
            });
            context.ProvisionalRecords.Add(new ProvisionalRecord
            {
                Id = Guid.CreateVersion7(),
                LibraryId = library.Id,
                SessionId = sessionId,
                SnapId = snapId,
                CropAssetId = assetId,
                TitleGuess = "Unreadable spine",
                MediaHint = ScanMediaHint.Books,
                Status = ProvisionalStatus.Unverified,
                CandidateId = candidateId,
                CreatedAt = DeletedAt,
            });
            await context.SaveChangesAsync();
            context.ScanCandidates.IgnoreQueryFilters().First(c => c.Id == candidateId).ProvisionalId =
                context.ProvisionalRecords.IgnoreQueryFilters().First(p => p.CandidateId == candidateId).Id;
            await context.SaveChangesAsync();
        }

        await using (var context = Db.CreateContext())
        {
            var time = new FakeTimeProvider(DeletedAt.AddDays(30).AddSeconds(1));
            var audit = new AuditLog(context, new CurrentUser(), new NoRequestOrigin(), time, new Uuid7IdGenerator(time), Options.Create(new AuditOptions()));
            await new PurgeAccount.Handler(context, time, new FakeAccounts(), files, [], new AnonymiseAuditActor.Handler(context), audit)
                .HandleAsync(new PurgeAccountPayload(library.OwnerId), CancellationToken.None);
        }

        await using (var context = Db.CreateContext())
        {
            (await context.ProvisionalRecords.IgnoreQueryFilters().CountAsync(p => p.LibraryId == library.Id)).ShouldBe(0);
            (await context.Assets.IgnoreQueryFilters().CountAsync(a => a.OwnerLibraryId == library.Id)).ShouldBe(0);
            (await context.ScanCandidates.IgnoreQueryFilters().CountAsync(c => c.LibraryId == library.Id)).ShouldBe(0);
        }

        files.Existing.ShouldBeEmpty();
    }
}
