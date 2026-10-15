using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Infrastructure.Audit;
using CollectionMaxxing.Infrastructure.Ids;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Deletion;

// LOCKED acceptance tests of LIB-2c (issue #67): a purge that fails halfway can be re-run and finishes without errors or orphans.
// Do not edit, weaken, skip or delete: add new tests instead.
public class PurgeAccountResumeTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset DeletedAt = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = DeletedAt.AddDays(30).AddSeconds(1);

    /// <summary>The identity side, in memory: when the account was deleted and what has been done to it.</summary>
    private sealed class FakeAccounts(DateTimeOffset? deletedAt) : IDeletedAccounts
    {
        public int Scrubs { get; private set; }

        public int Erasures { get; private set; }

        public Func<Task>? BeforeScrub { get; set; }

        public Task<DateTimeOffset?> DeletedAtAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(deletedAt);

        public async Task ScrubAuditTrailAsync(Guid userId, CancellationToken cancellationToken)
        {
            if (BeforeScrub is { } before)
            {
                await before();
            }

            Scrubs++;
        }

        public Task EraseAsync(Guid userId, CancellationToken cancellationToken)
        {
            Erasures++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFiles : IAccountFiles
    {
        public HashSet<string> Existing { get; } = [];

        public int FailOnCall { get; set; }

        private int _calls;

        public Task DeleteAsync(string blobPath, CancellationToken cancellationToken)
        {
            if (++_calls == FailOnCall)
            {
                throw new IOException("The disk went away.");
            }

            Existing.Remove(blobPath);
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyStep : ILibraryPurgeStep
    {
        public bool Fail { get; set; }

        public List<Guid> Libraries { get; } = [];

        public Task PurgeAsync(Guid libraryId, CancellationToken cancellationToken)
        {
            Libraries.Add(libraryId);
            return Fail ? throw new InvalidOperationException("The step failed.") : Task.CompletedTask;
        }
    }

    private async Task RunAsync(FakeAccounts accounts, FakeFiles files, Guid userId, DateTimeOffset now, params ILibraryPurgeStep[] steps)
    {
        var time = new FakeTimeProvider(now);
        await using var context = Db.CreateContext();
        var audit = new AuditLog(context, new CurrentUser(), new NoRequestOrigin(), time, new Uuid7IdGenerator(time), Options.Create(new AuditOptions()));
        await new PurgeAccount.Handler(context, time, accounts, files, steps, new AnonymiseAuditActor.Handler(context), audit)
            .HandleAsync(new PurgeAccountPayload(userId), CancellationToken.None);
    }

    /// <summary>A library whose only member is its owner, with something in each table that hangs from it.</summary>
    private async Task<(Guid LibraryId, Guid OwnerId, FakeFiles Files)> SoleOwnerLibraryAsync()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.LibraryId == library.Id && m.Role != LibraryRole.Owner).ExecuteDeleteAsync();
        }

        var parent = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var child = await TestData.CreateLocationAsync(Db, library.Id, "Box", parent);
        var itemId = await TestData.CreateItemAsync(Db, library, locationId: child);
        var tagId = await TestData.CreateTagAsync(Db, library.Id, "Horror");
        await TestData.AttachTagAsync(Db, library.Id, itemId, tagId);
        await TestData.CreateScanSessionAsync(Db, library);

        var files = new FakeFiles();
        await using (var context = Db.CreateContext())
        {
            foreach (var kind in new[] { AssetKind.Cover, AssetKind.UserPhoto, AssetKind.ScanPhoto })
            {
                var id = Guid.CreateVersion7();
                var path = $"ab/cd/{id:N}.jpg";
                files.Existing.Add(path);
                context.Assets.Add(new Asset { Id = id, Kind = kind, BlobPath = path, ContentType = "image/jpeg", Sha256 = id.ToString("N"), OwnerLibraryId = library.Id, CreatedAt = DeletedAt });
            }

            await context.SaveChangesAsync();
        }

        return (library.Id, library.OwnerId, files);
    }

    /// <summary>Every table that has rows for the library (any table with a <c>library_id</c> or <c>owner_library_id</c> column, and the library itself).</summary>
    private async Task<Dictionary<string, long>> RowsOfLibraryAsync(Guid libraryId)
    {
        var found = new Dictionary<string, long>();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        var targets = new List<(string Table, string Column)> { ("libraries", "id") };
        await using (var command = new NpgsqlCommand(
            "select table_name, column_name from information_schema.columns where table_schema = 'public' and column_name in ('library_id', 'owner_library_id') and table_name <> 'audit_events'",
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                targets.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (table, column) in targets)
        {
            await using var count = new NpgsqlCommand($"select count(*) from \"{table}\" where \"{column}\" = @id", connection);
            count.Parameters.AddWithValue("id", libraryId);
            var rows = (long)(await count.ExecuteScalarAsync())!;
            if (rows > 0)
            {
                found[$"{table}.{column}"] = rows;
            }
        }

        return found;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_purge_that_fails_halfway_through_the_database_can_be_run_again_and_finishes_with_nothing_left()
    {
        var (libraryId, ownerId, files) = await SoleOwnerLibraryAsync();
        var accounts = new FakeAccounts(DeletedAt);

        // The database refuses to delete tags: by then the scan rows, the items and the locations are gone, the library is not.
        await ExecuteAsync("""
            create function fail_tag_delete() returns trigger language plpgsql as $$ begin raise exception 'simulated crash'; end $$;
            create trigger fail_tag_delete before delete on tags for each row execute function fail_tag_delete();
            """);
        var crash = await Should.ThrowAsync<PostgresException>(() => RunAsync(accounts, files, ownerId, Due));
        crash.MessageText.ShouldContain("simulated crash");
        var halfway = await RowsOfLibraryAsync(libraryId);
        halfway.ShouldContainKey("libraries.id");
        halfway.ShouldContainKey("tags.library_id");
        halfway.ShouldNotContainKey("items.library_id");
        accounts.Erasures.ShouldBe(0, "the user is only erased once the data has gone");

        await ExecuteAsync("drop trigger fail_tag_delete on tags; drop function fail_tag_delete();");
        await RunAsync(accounts, files, ownerId, Due);

        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty("no row of the library is left in any table");
        files.Existing.ShouldBeEmpty();
        accounts.Erasures.ShouldBe(1);
        await using var context = Db.CreateContext();
        (await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == ownerId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_purge_that_fails_while_deleting_files_leaves_nothing_behind_when_run_again()
    {
        var (libraryId, ownerId, files) = await SoleOwnerLibraryAsync();
        files.FailOnCall = 2;
        var accounts = new FakeAccounts(DeletedAt);

        await Should.ThrowAsync<IOException>(() => RunAsync(accounts, files, ownerId, Due));
        files.Existing.Count.ShouldBe(2, "one file was deleted before the failure");
        (await RowsOfLibraryAsync(libraryId)).ShouldContainKey("assets.owner_library_id", "the rows that name the files are still there to be found again");

        await RunAsync(accounts, files, ownerId, Due);

        files.Existing.ShouldBeEmpty("no orphaned files");
        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_purge_that_fails_in_another_features_step_is_picked_up_again()
    {
        var (libraryId, ownerId, files) = await SoleOwnerLibraryAsync();
        var step = new FlakyStep { Fail = true };
        var accounts = new FakeAccounts(DeletedAt);

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(accounts, files, ownerId, Due, step));
        (await RowsOfLibraryAsync(libraryId)).ShouldContainKey("items.library_id");

        step.Fail = false;
        await RunAsync(accounts, files, ownerId, Due, step);

        step.Libraries.ShouldBe([libraryId, libraryId]);
        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_purge_that_stops_before_the_audit_trail_is_cleaned_finishes_it_on_the_next_run()
    {
        var (libraryId, ownerId, files) = await SoleOwnerLibraryAsync();
        await using (var context = Db.CreateContext())
        {
            context.AuditEvents.Add(new AuditEvent { Id = Guid.CreateVersion7(), ActorUserId = ownerId, LibraryId = libraryId, Action = "item.deleted", At = DeletedAt });
            await context.SaveChangesAsync();
        }

        var accounts = new FakeAccounts(DeletedAt) { BeforeScrub = () => throw new InvalidOperationException("Stopped.") };
        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(accounts, files, ownerId, Due));
        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty("the library had already gone");
        await using (var context = Db.CreateContext())
        {
            (await context.AuditEvents.CountAsync(e => e.ActorUserId == ownerId)).ShouldBe(1);
        }

        accounts.BeforeScrub = null;
        await RunAsync(accounts, files, ownerId, Due);

        await using var read = Db.CreateContext();
        (await read.AuditEvents.CountAsync(e => e.ActorUserId == ownerId)).ShouldBe(0);
        (await read.AuditEvents.CountAsync(e => e.Action == "item.deleted")).ShouldBe(1, "the event itself stays");
        accounts.Erasures.ShouldBe(1);
    }

    [Fact]
    public async Task Running_a_finished_purge_again_changes_nothing_and_does_not_fail()
    {
        var (libraryId, ownerId, files) = await SoleOwnerLibraryAsync();
        var accounts = new FakeAccounts(DeletedAt);
        await RunAsync(accounts, files, ownerId, Due);

        await RunAsync(accounts, files, ownerId, Due);

        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty();
        await using var context = Db.CreateContext();
        (await context.Libraries.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Before_30_days_and_for_an_account_that_was_never_deleted_the_purge_does_nothing()
    {
        var (libraryId, ownerId, files) = await SoleOwnerLibraryAsync();
        var before = await RowsOfLibraryAsync(libraryId);

        await RunAsync(new FakeAccounts(DeletedAt), files, ownerId, DeletedAt.AddDays(30).AddSeconds(-1));
        await RunAsync(new FakeAccounts(null), files, ownerId, DeletedAt.AddYears(1));

        (await RowsOfLibraryAsync(libraryId)).ShouldBe(before);
        files.Existing.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_library_that_still_has_other_members_is_never_purged_but_the_users_place_in_it_goes()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateItemAsync(Db, library);

        await RunAsync(new FakeAccounts(DeletedAt), new FakeFiles(), library.OwnerId, Due);

        var rows = await RowsOfLibraryAsync(library.Id);
        rows["libraries.id"].ShouldBe(1);
        rows["items.library_id"].ShouldBe(1);
        rows["library_members.library_id"].ShouldBe(2, "the editor and the viewer stay; the owner's place is gone");
    }
}
