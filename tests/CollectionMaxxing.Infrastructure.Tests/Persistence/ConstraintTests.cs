using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

/// <summary>The database's own rules for the tables the streams will fill in: uniqueness, value checks and tenancy.</summary>
public class ConstraintTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static ScanCandidate NewCandidate(TestLibrary library, Guid sessionId, int seq, string? idempotencyKey = null) => new()
    {
        Id = Guid.CreateVersion7(),
        LibraryId = library.Id,
        SessionId = sessionId,
        Seq = seq,
        Source = CandidateSource.Barcode,
        RawCode = "9780441172719",
        Resolution = CandidateResolution.Pending,
        Decision = CandidateDecision.Undecided,
        IdempotencyKey = idempotencyKey,
        CreatedAt = Now,
    };

    // ---- values the database refuses --------------------------------------------------------------------------

    [Theory]
    [InlineData("items", "status")]
    [InlineData("items", "cover_choice")]
    [InlineData("items", "added_via")]
    public async Task An_item_column_refuses_text_that_is_not_one_of_its_values(string table, string column)
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateItemAsync(Db, library);

        await SchemaAssert.FailsWithAsync(
            PostgresErrorCodes.CheckViolation,
            () => ExecuteAsync($"update {table} set {column} = 'bogus'"));
    }

    [Fact]
    public async Task An_item_can_have_no_condition_but_not_a_made_up_one()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateItemAsync(Db, library);

        await ExecuteAsync("update items set condition = null, completeness = 'n_a'");
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.CheckViolation, () => ExecuteAsync("update items set condition = 'pristine'"));
    }

    [Fact]
    public async Task A_release_format_is_one_of_the_known_formats()
    {
        await TestData.CreateReleaseAsync(Db);

        await ExecuteAsync("update releases set format = 'uhd_bluray', region = 'ntsc_u'");
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.CheckViolation, () => ExecuteAsync("update releases set format = 'laser'"));
    }

    [Fact]
    public async Task An_invite_can_never_make_an_owner()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        await using var context = Db.CreateContext();
        context.LibraryInvites.Add(new LibraryInvite
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            Role = LibraryRole.Owner,
            Name = "Sneaky",
            TokenHash = "hash",
            CreatedBy = library.OwnerId,
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(7),
        });

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.CheckViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_candidates_box_is_four_numbers_between_0_and_1_or_nothing()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);

        await using var context = Db.CreateContext();
        var candidate = NewCandidate(library, sessionId, 1);
        context.ScanCandidates.Add(candidate);
        await context.SaveChangesAsync();

        await ExecuteAsync("update scan_candidates set box_x = 0.1, box_y = 0.2, box_w = 0.3, box_h = 0.4");
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.CheckViolation, () => ExecuteAsync("update scan_candidates set box_x = 1.5"));
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.CheckViolation, () => ExecuteAsync("update scan_candidates set box_x = null"));
    }

    // ---- uniqueness -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_barcode_can_belong_to_two_releases_but_only_once_to_each()
    {
        var first = await TestData.CreateReleaseAsync(Db);
        var second = await TestData.CreateReleaseAsync(Db);

        await using (var context = Db.CreateContext())
        {
            context.ReleaseIdentifiers.AddRange(
                new ReleaseIdentifier { ReleaseId = first, Type = IdentifierType.Gtin13, Value = "0045496870010" },
                new ReleaseIdentifier { ReleaseId = second, Type = IdentifierType.Gtin13, Value = "0045496870010" });
            await context.SaveChangesAsync();
        }

        await using var duplicate = Db.CreateContext();
        duplicate.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = first, Type = IdentifierType.Gtin13, Value = "0045496870010" });
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task A_retried_candidate_with_the_same_idempotency_key_is_refused_but_candidates_without_a_key_are_not()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using (var context = Db.CreateContext())
        {
            context.ScanCandidates.AddRange(
                NewCandidate(library, sessionId, 1),
                NewCandidate(library, sessionId, 2),
                NewCandidate(library, sessionId, 3, "key-1"));
            await context.SaveChangesAsync();
        }

        await using var retry = Db.CreateContext();
        retry.ScanCandidates.Add(NewCandidate(library, sessionId, 4, "key-1"));
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => retry.SaveChangesAsync());
    }

    [Fact]
    public async Task Candidate_sequence_numbers_are_unique_within_a_session()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using (var context = Db.CreateContext())
        {
            context.ScanCandidates.Add(NewCandidate(library, sessionId, 1));
            await context.SaveChangesAsync();
        }

        await using var again = Db.CreateContext();
        again.ScanCandidates.Add(NewCandidate(library, sessionId, 1));
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => again.SaveChangesAsync());
    }

    [Fact]
    public async Task Tag_names_are_unique_per_library_ignoring_case()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        await using (var context = Db.CreateContext())
        {
            context.Tags.AddRange(
                new Tag { Id = Guid.CreateVersion7(), LibraryId = mine.Id, Name = "Sci-Fi" },
                new Tag { Id = Guid.CreateVersion7(), LibraryId = theirs.Id, Name = "Sci-Fi" });
            await context.SaveChangesAsync();
        }

        await using var duplicate = Db.CreateContext();
        duplicate.Tags.Add(new Tag { Id = Guid.CreateVersion7(), LibraryId = mine.Id, Name = "sci-fi" });
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => duplicate.SaveChangesAsync());
    }

    // ---- tenancy ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_cannot_be_put_on_an_item_of_another_library()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var myItem = await TestData.CreateItemAsync(Db, mine);
        var theirTag = Guid.CreateVersion7();
        await using (var context = Db.CreateContext())
        {
            context.Tags.Add(new Tag { Id = theirTag, LibraryId = theirs.Id, Name = "Theirs" });
            await context.SaveChangesAsync();
        }

        await using var tagging = Db.CreateContext();
        tagging.ItemTags.Add(new ItemTag { LibraryId = mine.Id, ItemId = myItem, TagId = theirTag });
        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => tagging.SaveChangesAsync());
    }

    [Fact]
    public async Task A_candidate_cannot_join_a_session_of_another_library()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var theirSession = await TestData.CreateScanSessionAsync(Db, theirs);

        await using var context = Db.CreateContext();
        context.ScanCandidates.Add(NewCandidate(mine, theirSession, 1));

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_scan_session_cannot_default_to_a_location_of_another_library()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var theirRoom = await TestData.CreateLocationAsync(Db, theirs.Id, "Room");

        await using var context = Db.CreateContext();
        context.ScanSessions.Add(new ScanSession
        {
            Id = Guid.CreateVersion7(),
            LibraryId = mine.Id,
            UserId = mine.EditorId,
            Status = ScanSessionStatus.Open,
            DefaultLocationId = theirRoom,
            CreatedAt = Now,
        });

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_release_a_library_made_for_itself_is_visible_only_to_its_members()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var global = await TestData.CreateReleaseAsync(Db);
        var privateToMine = await TestData.CreateReleaseAsync(Db, mine.Id);

        await using var asMine = Db.CreateContext(mine.ActingAs(LibraryRole.Viewer));
        await using var asTheirs = Db.CreateContext(theirs.ActingAs(LibraryRole.Viewer));
        await using var asNobody = Db.CreateContext();

        (await asMine.Releases.Select(r => r.Id).ToListAsync()).ShouldBe([global, privateToMine], ignoreOrder: true);
        (await asTheirs.Releases.Select(r => r.Id).ToListAsync()).ShouldBe([global]);
        (await asNobody.Releases.Select(r => r.Id).ToListAsync()).ShouldBe([global]);
    }

    [Fact]
    public async Task Items_are_hidden_from_other_libraries_like_locations_are()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var mineItem = await TestData.CreateItemAsync(Db, mine);
        await TestData.CreateItemAsync(Db, theirs);

        await using var context = Db.CreateContext(mine.ActingAs(LibraryRole.Editor));

        (await context.Items.Select(i => i.Id).ToListAsync()).ShouldBe([mineItem]);
    }

    [Fact]
    public async Task Deleting_a_library_takes_everything_inside_it_along()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        await TestData.CreateItemAsync(Db, library, locationId: room);
        await TestData.CreateScanSessionAsync(Db, library);

        await ExecuteAsync($"delete from libraries where id = '{library.Id}'");

        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "items", "locations", "scan_sessions", "library_members" })
        {
            await using var count = new NpgsqlCommand($"select count(*) from {table}", connection);
            ((long)(await count.ExecuteScalarAsync())!).ShouldBe(0, table);
        }
    }
}
