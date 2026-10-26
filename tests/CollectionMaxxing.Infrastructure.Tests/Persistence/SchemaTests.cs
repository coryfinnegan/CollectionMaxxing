using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

public class SchemaTests(PostgresFixture db) : DatabaseTest(db)
{
    /// <summary>Every table of the v1 schema (docs/03), including those ASP.NET Core Identity brings along. No OpenIddict, passkey or pairing table is left (AUTH-1).</summary>
    private static readonly string[] ExpectedTables =
    [
        // Identity and tenancy
        "users", "roles", "user_roles", "user_claims", "role_claims", "user_logins", "user_tokens",
        "web_sessions", "app_sign_ins", "setup_codes", "libraries", "library_members", "library_invites",

        // Catalogue
        "works", "releases", "release_identifiers", "assets", "platforms", "provider_cache", "provider_quota",

        // Your stuff
        "items", "locations", "tags", "item_tags", "item_feature_prints", "item_tombstones",

        // Scanning and operations
        "scan_sessions", "scan_snaps", "scan_candidates", "scan_session_change_counters", "provisional_records", "jobs", "match_feedback", "audit_events", "idempotency_keys",
    ];

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    [Fact]
    public async Task Migrations_create_exactly_the_tables_of_docs_03_with_snake_case_names()
    {
        var tables = await QueryStringsAsync("select table_name from information_schema.tables where table_schema = 'public' order by 1");

        tables.ShouldBe([.. ExpectedTables, "__EFMigrationsHistory"], ignoreOrder: true);
    }

    [Fact]
    public void The_migration_snapshot_matches_the_model()
    {
        // docs/03: CI checks that the model snapshot has no pending changes. Add a migration when the model changes.
        using var context = Db.CreateContext();

        context.Database.HasPendingModelChanges().ShouldBeFalse("Run `dotnet ef migrations add <Name>` for the model change.");
    }

    [Fact]
    public async Task Columns_use_snake_case()
    {
        var columns = await QueryStringsAsync("select column_name from information_schema.columns where table_name = 'locations' order by 1");

        columns.ShouldBe(["id", "library_id", "name", "parent_id", "sort_order"], ignoreOrder: true);
    }

    [Fact]
    public async Task Every_table_and_column_name_is_lower_snake_case()
    {
        var offenders = await QueryStringsAsync(
            """
            select table_name || '.' || column_name from information_schema.columns
            where table_schema = 'public' and table_name <> '__EFMigrationsHistory' and column_name !~ '^[a-z][a-z0-9_]*$'
            """);

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_trigram_and_case_insensitive_text_extensions_are_installed()
    {
        var extensions = await QueryStringsAsync("select extname from pg_extension");

        extensions.ShouldContain("pg_trgm");
        extensions.ShouldContain("citext");
    }

    [Fact]
    public async Task Titles_are_searchable_by_trigram_similarity_through_gin_indexes()
    {
        var definitions = await QueryStringsAsync("select indexdef from pg_indexes where schemaname = 'public'");

        definitions.ShouldContain(d => d.Contains("ON public.works USING gin (normalized_title gin_trgm_ops)", StringComparison.Ordinal));
        definitions.ShouldContain(d => d.Contains("ON public.items USING gin (custom_title gin_trgm_ops)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Auth0_identities_are_linked_to_users_one_subject_at_most_once()
    {
        var columns = await QueryStringsAsync("select column_name from information_schema.columns where table_name = 'user_logins'");
        var indexes = await QueryStringsAsync("select indexdef from pg_indexes where tablename = 'user_logins'");

        columns.ShouldBe(["created_at", "email", "id", "last_used_at", "provider", "subject", "user_id"], ignoreOrder: true);
        indexes.ShouldContain(d => d.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal) && d.EndsWith("(subject)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_OpenIddict_passkey_or_pairing_table_is_left()
    {
        var tables = await QueryStringsAsync("select table_name from information_schema.tables where table_schema = 'public'");

        tables.ShouldNotContain(t => t.StartsWith("openiddict_", StringComparison.Ordinal));
        tables.ShouldNotContain("user_passkeys");
        tables.ShouldNotContain("device_pairings");
    }

    [Fact]
    public async Task Web_sessions_are_kept_per_user_with_when_they_started_were_last_seen_and_were_revoked()
    {
        var nullable = await QueryStringsAsync(
            "select column_name || ':' || is_nullable from information_schema.columns where table_name = 'web_sessions'");
        var foreignKeys = await QueryStringsAsync(
            """
            select ccu.table_name || '.' || kcu.column_name || ':' || rc.delete_rule
            from information_schema.referential_constraints rc
            join information_schema.key_column_usage kcu on kcu.constraint_name = rc.constraint_name
            join information_schema.constraint_column_usage ccu on ccu.constraint_name = rc.unique_constraint_name
            where kcu.table_name = 'web_sessions'
            """);

        nullable.ShouldBe(["id:NO", "user_id:NO", "label:NO", "created_at:NO", "last_seen_at:NO", "revoked_at:YES"], ignoreOrder: true);
        foreignKeys.ShouldBe(["users.user_id:CASCADE"]);
    }

    [Fact]
    public async Task Users_have_a_display_name_and_an_optional_email()
    {
        var nullable = await QueryStringsAsync(
            "select column_name || ':' || is_nullable from information_schema.columns where table_name = 'users'");

        nullable.ShouldContain("display_name:NO");
        nullable.ShouldContain("email:YES");
        nullable.ShouldContain("deleted_at:YES");
    }

    [Fact]
    public async Task Roles_are_stored_as_lowercase_text()
    {
        await TestData.CreateLibraryAsync(Db);

        var roles = await QueryStringsAsync("select role from library_members order by 1");

        roles.ShouldBe(["editor", "owner", "viewer"]);
    }

    [Fact]
    public async Task A_library_can_have_only_one_owner()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var anotherUser = await TestData.CreateUserAsync(Db);

        await using var context = Db.CreateContext();
        context.LibraryMembers.Add(new LibraryMember(library.Id, anotherUser, LibraryRole.Owner, DateTimeOffset.UnixEpoch));

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_member_must_be_a_real_user()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        await using var context = Db.CreateContext();
        context.LibraryMembers.Add(new LibraryMember(library.Id, Guid.CreateVersion7(), LibraryRole.Viewer, DateTimeOffset.UnixEpoch));

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_top_level_locations_in_one_library_cannot_share_a_name()
    {
        // NULL parents must count as equal, or the rule would silently not apply to top-level locations.
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, library.Id, "Garage");

        await using var context = Db.CreateContext();
        var tree = new LocationTree(library.Id, []);
        context.Locations.Add(tree.Add(Guid.CreateVersion7(), null, "Garage", null).Value);

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Location_names_are_unique_ignoring_case_even_when_the_domain_check_is_bypassed()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, library.Id, "Garage");

        await using var context = Db.CreateContext();
        var tree = new LocationTree(library.Id, []);
        context.Locations.Add(tree.Add(Guid.CreateVersion7(), null, "GARAGE", null).Value);

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_libraries_can_use_the_same_top_level_name()
    {
        var first = await TestData.CreateLibraryAsync(Db);
        var second = await TestData.CreateLibraryAsync(Db);

        await TestData.CreateLocationAsync(Db, first.Id, "Garage");
        await TestData.CreateLocationAsync(Db, second.Id, "Garage");
    }

    [Fact]
    public async Task A_location_that_still_has_children_cannot_be_deleted_from_the_database()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Owner));
        var roomRow = await context.Locations.SingleAsync(l => l.Id == room);
        context.Locations.Remove(roomRow);

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_location_cannot_have_a_parent_in_another_library_even_when_the_domain_check_is_bypassed()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var theirRoom = await TestData.CreateLocationAsync(Db, theirs.Id, "Room");

        await using var context = Db.CreateContext();
        var hijacked = new LocationTree(mine.Id, []).Add(Guid.CreateVersion7(), null, "Shelf", null).Value;
        context.Locations.Add(hijacked);
        context.Entry(hijacked).Property(l => l.ParentId).CurrentValue = theirRoom;

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task An_item_cannot_be_put_in_a_location_of_another_library()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var theirRoom = await TestData.CreateLocationAsync(Db, theirs.Id, "Room");

        await SchemaAssert.FailsWithAsync(
            PostgresErrorCodes.ForeignKeyViolation,
            () => TestData.CreateItemAsync(Db, mine, locationId: theirRoom));
    }

    [Fact]
    public async Task An_item_can_be_put_in_a_location_of_its_own_library()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");

        await TestData.CreateItemAsync(Db, library, locationId: room);
    }

    [Fact]
    public async Task A_location_that_holds_items_cannot_be_deleted()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        await TestData.CreateItemAsync(Db, library, locationId: room);

        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Owner));
        context.Locations.Remove(await context.Locations.SingleAsync(l => l.Id == room));

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Items_arrive_edition_verified_unless_said_otherwise()
    {
        // The column default is the backfill (issue #343): it wrote true into every pre-existing row when the
        // migration added the column, and still answers for any insert that does not name it.
        var defaults = await QueryStringsAsync(
            "select column_default from information_schema.columns where table_name = 'items' and column_name = 'edition_verified'");

        defaults.ShouldBe(["true"]);

        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand(
            "insert into items (id, library_id, release_id, status, cover_choice, added_via, created_at, created_by, updated_at) " +
            "values ($1, $2, $3, 'owned', 'release', 'manual', now(), $4, now())", connection))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(library.Id);
            command.Parameters.AddWithValue(releaseId);
            command.Parameters.AddWithValue(library.OwnerId);
            await command.ExecuteNonQueryAsync();
        }

        await using var read = new NpgsqlCommand("select edition_verified from items where id = $1", connection);
        read.Parameters.AddWithValue(id);

        (await read.ExecuteScalarAsync()).ShouldBe(true);
    }
}
