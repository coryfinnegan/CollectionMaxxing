using System.Reflection;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace CollectionMaxxing.Architecture.Tests.MigrationSafety;

/// <summary>
/// A migration must leave the previous release able to run.
///
/// The deployer migrates the database, then starts the new image. When the new image fails its smoke test it goes back to the
/// previous image, which then runs against the already migrated schema: migrations are never reversed. So everything a migration
/// does has to be something the previous release's SQL survives. This test reads each migration's up operations (EF's operation
/// model; raw <c>Sql</c> by text) and fails on:
///
///   - a NOT NULL column added with no database default (the previous INSERT does not list it);
///   - a column or table dropped or renamed (the previous release still names it);
///   - the default of a NOT NULL column removed (the previous INSERT leans on it);
///   - a nullable column made NOT NULL (the previous release may still write NULL, default or not).
///
/// Not looked at: column type changes, new CHECK, unique or foreign-key constraints on columns the previous release writes, and a
/// dropped primary key or unique index that a previous ON CONFLICT names. Read those in review.
///
/// A deliberate break is possible: a step of a two-release change whose first half already shipped, or a column nothing reads any
/// more. Add a line to <see cref="RollbackWaivers.All"/> with the migration id, the risks you accept and the reason. The waiver
/// covers only the risks it names, so it cannot hide another problem in the same migration, and it fails once it no longer
/// matches anything (the migration was renamed, or no longer does that).
///
/// The rule is for new migrations. Those from before it existed are listed in <see cref="RollbackWaivers.All"/> as deployed long ago.
/// </summary>
public class RollbackSafeMigrationsTests
{
    private const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // ---- The migrations of the repository ----

    [Fact]
    public void Every_migration_of_the_repository_is_inspected()
    {
        var migrations = Migrations();

        migrations.Count.ShouldBeGreaterThan(10);
        migrations.Select(m => m.Id).ShouldContain("20261013153821_InitialCreate");
        migrations.Select(m => m.Id).ShouldBeUnique();
        migrations.ShouldAllBe(m => m.Up.Count > 0);
    }

    [Fact]
    public void No_migration_breaks_the_previous_release_on_rollback()
    {
        var offenders = Migrations()
            .SelectMany(m => RollbackSafety.Unwaived(m.Id, RollbackSafety.Check(m.Up), RollbackWaivers.All).Select(f => $"{m.Id}: {f.Operation}"))
            .ToList();

        offenders.ShouldBeEmpty(
            "The previous release has to survive this schema, because a failed deploy rolls back to it without reversing the migration. "
            + "Give the column a default, keep it nullable, or split the change over two releases. "
            + "If breaking it is deliberate, add a waiver with the reason to MigrationSafety/RollbackWaivers.cs.");
    }

    [Fact]
    public void Every_waiver_names_a_migration_and_the_risks_it_still_has_and_says_why()
    {
        var migrations = Migrations().ToDictionary(m => m.Id, m => RollbackSafety.Check(m.Up));
        var problems = new List<string>();

        foreach (var waiver in RollbackWaivers.All)
        {
            if (string.IsNullOrWhiteSpace(waiver.Reason))
            {
                problems.Add($"{waiver.MigrationId}: no reason");
            }

            if (waiver.Risks.Length == 0)
            {
                problems.Add($"{waiver.MigrationId}: names no risk");
            }

            if (!migrations.TryGetValue(waiver.MigrationId, out var findings))
            {
                problems.Add($"{waiver.MigrationId}: no such migration");
                continue;
            }

            problems.AddRange(waiver.Risks.Where(risk => findings.All(f => f.Risk != risk)).Select(risk => $"{waiver.MigrationId}: no longer has {risk}"));
        }

        RollbackWaivers.All.Select(w => w.MigrationId).ShouldBeUnique("Give a migration one waiver naming all its risks.");
        problems.ShouldBeEmpty("Delete the waiver or correct it.");
    }

    // ---- Adding a column ----

    [Fact]
    public void A_required_column_with_no_default_fails()
    {
        var findings = Check(b => b.AddColumn<Guid>(name: "id", table: "release_identifiers", type: "uuid", nullable: false));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.RequiredColumnWithoutDefault]);
        findings[0].Operation.ShouldContain("release_identifiers.id");
    }

    [Fact]
    public void A_required_column_with_a_default_value_passes()
    {
        Check(b => b.AddColumn<bool>(name: "verified", table: "items", type: "boolean", nullable: false, defaultValue: false)).ShouldBeEmpty();
    }

    [Fact]
    public void A_required_column_with_a_default_expression_passes()
    {
        Check(b => b.AddColumn<Guid>(name: "id", table: "release_identifiers", type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()")).ShouldBeEmpty();
    }

    [Fact]
    public void A_required_identity_column_passes_because_the_database_fills_it()
    {
        Check(b => b.AddColumn<int>(name: "number", table: "items", type: "integer", nullable: false)
            .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)).ShouldBeEmpty();
    }

    [Fact]
    public void A_required_computed_column_passes_because_the_database_fills_it()
    {
        Check(b => b.AddColumn<string>(name: "slug", table: "items", type: "text", nullable: false, computedColumnSql: "lower(name)", stored: true)).ShouldBeEmpty();
    }

    [Fact]
    public void A_nullable_column_passes()
    {
        Check(b => b.AddColumn<Guid>(name: "library_id", table: "release_identifiers", type: "uuid", nullable: true)).ShouldBeEmpty();
    }

    [Fact]
    public void A_column_added_nullable_then_made_required_with_no_default_in_the_same_migration_fails()
    {
        var findings = Check(b =>
        {
            b.AddColumn<string>(name: "channel", table: "scan_sessions", type: "character varying(32)", maxLength: 32, nullable: true);
            b.Sql("update scan_sessions set channel = 'home';");
            b.AlterColumn<string>(name: "channel", table: "scan_sessions", type: "character varying(32)", maxLength: 32, nullable: false);
        });

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.RequiredColumnWithoutDefault]);
        findings[0].Operation.ShouldContain("scan_sessions.channel");
    }

    [Fact]
    public void A_column_added_nullable_then_made_required_with_a_default_in_the_same_migration_passes()
    {
        Check(b =>
        {
            b.AddColumn<string>(name: "channel", table: "scan_sessions", type: "character varying(32)", maxLength: 32, nullable: true);
            b.Sql("update scan_sessions set channel = 'home';");
            b.AlterColumn<string>(name: "channel", table: "scan_sessions", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "home");
        }).ShouldBeEmpty();
    }

    [Fact]
    public void A_column_added_nullable_and_left_nullable_passes()
    {
        Check(b =>
        {
            b.AddColumn<string>(name: "channel", table: "scan_sessions", type: "character varying(32)", maxLength: 32, nullable: true);
            b.AlterColumn<string>(name: "channel", table: "scan_sessions", type: "character varying(64)", maxLength: 64, nullable: true);
        }).ShouldBeEmpty();
    }

    [Fact]
    public void A_required_column_of_a_table_created_by_the_same_migration_passes()
    {
        Check(b =>
        {
            b.CreateTable(
                name: "shelves",
                columns: table => new { id = table.Column<Guid>(type: "uuid", nullable: false) },
                constraints: table => table.PrimaryKey("pk_shelves", x => x.id));
            b.AddColumn<string>(name: "label", table: "shelves", type: "text", nullable: false);
        }).ShouldBeEmpty();
    }

    [Fact]
    public void A_required_column_of_a_table_with_the_same_name_in_another_schema_fails()
    {
        var findings = Check(b =>
        {
            b.CreateTable(
                name: "shelves",
                schema: "other",
                columns: table => new { id = table.Column<Guid>(type: "uuid", nullable: false) },
                constraints: table => table.PrimaryKey("pk_shelves", x => x.id));
            b.AddColumn<string>(name: "label", table: "shelves", type: "text", nullable: false);
        });

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.RequiredColumnWithoutDefault]);
    }

    // ---- Dropping and renaming ----

    [Fact]
    public void Dropping_a_column_fails()
    {
        Check(b => b.DropColumn(name: "notes", table: "items")).Select(f => f.Risk).ShouldBe([RollbackRisk.DropsOrRenames]);
    }

    [Fact]
    public void Renaming_a_column_fails()
    {
        Check(b => b.RenameColumn(name: "notes", table: "items", newName: "remarks")).Select(f => f.Risk).ShouldBe([RollbackRisk.DropsOrRenames]);
    }

    [Fact]
    public void Dropping_a_table_fails()
    {
        Check(b => b.DropTable(name: "match_feedback")).Select(f => f.Risk).ShouldBe([RollbackRisk.DropsOrRenames]);
    }

    [Fact]
    public void Renaming_a_table_fails()
    {
        Check(b => b.RenameTable(name: "match_feedback", newName: "feedback")).Select(f => f.Risk).ShouldBe([RollbackRisk.DropsOrRenames]);
    }

    [Fact]
    public void Dropping_an_index_or_a_constraint_passes()
    {
        Check(b =>
        {
            b.DropIndex(name: "ix_items_notes", table: "items");
            b.DropCheckConstraint(name: "ck_scan_snaps_engine", table: "scan_snaps");
            b.DropForeignKey(name: "fk_items_locations_location_id", table: "items");
        }).ShouldBeEmpty();
    }

    // ---- Altering a column ----

    [Fact]
    public void Removing_the_default_of_a_required_column_fails()
    {
        var findings = Check(b => b.AlterColumn<Guid>(
            name: "id", table: "release_identifiers", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: false, oldDefaultValueSql: "gen_random_uuid()"));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.DropsDefaultOfRequiredColumn]);
    }

    [Fact]
    public void Removing_a_default_value_of_a_required_column_fails()
    {
        var findings = Check(b => b.AlterColumn<bool>(
            name: "verified", table: "items", type: "boolean", nullable: false,
            oldClrType: typeof(bool), oldType: "boolean", oldNullable: false, oldDefaultValue: false));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.DropsDefaultOfRequiredColumn]);
    }

    [Fact]
    public void Removing_the_default_of_a_nullable_column_passes()
    {
        Check(b => b.AlterColumn<bool>(
            name: "verified", table: "items", type: "boolean", nullable: true,
            oldClrType: typeof(bool), oldType: "boolean", oldNullable: true, oldDefaultValue: false)).ShouldBeEmpty();
    }

    [Fact]
    public void Changing_the_default_of_a_required_column_passes()
    {
        Check(b => b.AlterColumn<int>(
            name: "attempts", table: "jobs", type: "integer", nullable: false, defaultValue: 3,
            oldClrType: typeof(int), oldType: "integer", oldNullable: false, oldDefaultValue: 0)).ShouldBeEmpty();
    }

    [Fact]
    public void Making_a_nullable_column_required_fails()
    {
        var findings = Check(b => b.AlterColumn<string>(
            name: "title", table: "works", type: "text", nullable: false,
            oldClrType: typeof(string), oldType: "text", oldNullable: true));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.MakesColumnRequired]);
    }

    [Fact]
    public void Making_a_nullable_column_required_fails_even_with_a_default_because_the_previous_release_may_write_null()
    {
        var findings = Check(b => b.AlterColumn<string>(
            name: "title", table: "works", type: "text", nullable: false, defaultValue: "",
            oldClrType: typeof(string), oldType: "text", oldNullable: true));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.MakesColumnRequired]);
    }

    [Fact]
    public void Making_a_required_column_nullable_passes()
    {
        Check(b => b.AlterColumn<string>(
            name: "title", table: "works", type: "text", nullable: true,
            oldClrType: typeof(string), oldType: "text", oldNullable: false)).ShouldBeEmpty();
    }

    [Fact]
    public void Widening_a_required_column_passes()
    {
        Check(b => b.AlterColumn<string>(
            name: "title", table: "works", type: "character varying(500)", maxLength: 500, nullable: false,
            oldClrType: typeof(string), oldType: "character varying(200)", oldMaxLength: 200, oldNullable: false)).ShouldBeEmpty();
    }

    // ---- Raw SQL ----

    [Theory]
    [InlineData("ALTER TABLE items DROP COLUMN notes;", RollbackRisk.DropsOrRenames)]
    [InlineData("alter table items drop column if exists notes;", RollbackRisk.DropsOrRenames)]
    [InlineData("DROP TABLE match_feedback;", RollbackRisk.DropsOrRenames)]
    [InlineData("DROP TABLE IF EXISTS match_feedback CASCADE;", RollbackRisk.DropsOrRenames)]
    [InlineData("ALTER TABLE items RENAME COLUMN notes TO remarks;", RollbackRisk.DropsOrRenames)]
    [InlineData("ALTER TABLE items RENAME TO things;", RollbackRisk.DropsOrRenames)]
    [InlineData("ALTER TABLE release_identifiers ALTER COLUMN id DROP DEFAULT;", RollbackRisk.DropsDefaultOfRequiredColumn)]
    [InlineData("ALTER TABLE items\n  ALTER COLUMN notes\n  DROP   DEFAULT;", RollbackRisk.DropsDefaultOfRequiredColumn)]
    [InlineData("ALTER TABLE items ALTER COLUMN notes SET NOT NULL;", RollbackRisk.MakesColumnRequired)]
    [InlineData("ALTER TABLE items ADD COLUMN number integer NOT NULL;", RollbackRisk.RequiredColumnWithoutDefault)]
    [InlineData("ALTER TABLE items ADD number integer NOT NULL;", RollbackRisk.RequiredColumnWithoutDefault)]
    [InlineData("ALTER TABLE items ADD COLUMN IF NOT EXISTS number numeric(10,2) NOT NULL;", RollbackRisk.RequiredColumnWithoutDefault)]
    [InlineData("ALTER TABLE items ADD COLUMN \"number\" integer NOT NULL", RollbackRisk.RequiredColumnWithoutDefault)]
    public void Raw_sql_that_breaks_the_previous_release_fails(string sql, RollbackRisk risk)
    {
        var findings = Check(b => b.Sql(sql));

        findings.Select(f => f.Risk).ShouldBe([risk]);
    }

    [Theory]
    [InlineData("ALTER TABLE items ADD COLUMN number integer NOT NULL DEFAULT 0;")]
    [InlineData("ALTER TABLE release_identifiers ADD COLUMN id uuid NOT NULL DEFAULT gen_random_uuid();")]
    [InlineData("ALTER TABLE items ADD COLUMN number integer DEFAULT 0 NOT NULL;")]
    [InlineData("ALTER TABLE items ADD COLUMN number integer NOT NULL GENERATED ALWAYS AS IDENTITY;")]
    [InlineData("ALTER TABLE items ADD COLUMN number bigserial NOT NULL;")]
    [InlineData("ALTER TABLE items ADD COLUMN number integer;")]
    [InlineData("ALTER TABLE items ADD COLUMN number integer NULL;")]
    [InlineData("ALTER TABLE items ADD CONSTRAINT ck_items_number CHECK (number > 0) NOT VALID;")]
    [InlineData("ALTER TABLE items ADD PRIMARY KEY (id);")]
    [InlineData("ALTER TABLE items ALTER COLUMN notes DROP NOT NULL;")]
    [InlineData("ALTER TABLE items ALTER COLUMN notes SET DEFAULT '';")]
    [InlineData("ALTER TABLE items DROP CONSTRAINT ck_items_number;")]
    [InlineData("DROP INDEX ix_items_notes;")]
    [InlineData("UPDATE items SET number = 1 WHERE number IS NULL;")]
    [InlineData("CREATE UNIQUE INDEX ux_items_number ON items (number) WHERE number IS NOT NULL;")]
    public void Raw_sql_the_previous_release_survives_passes(string sql)
    {
        Check(b => b.Sql(sql)).ShouldBeEmpty();
    }

    [Fact]
    public void Raw_sql_is_judged_one_column_at_a_time()
    {
        var findings = Check(b => b.Sql("ALTER TABLE items ADD COLUMN a integer NOT NULL DEFAULT 0, ADD COLUMN b integer NOT NULL, ADD COLUMN c numeric(10, 2) NOT NULL DEFAULT 1;"));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.RequiredColumnWithoutDefault]);
        findings[0].Operation.ShouldContain("ADD COLUMN b");
    }

    [Fact]
    public void Raw_sql_is_judged_one_statement_at_a_time()
    {
        var findings = Check(b => b.Sql("ALTER TABLE items ADD COLUMN a integer NOT NULL DEFAULT 0; ALTER TABLE items ALTER COLUMN a DROP DEFAULT;"));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.DropsDefaultOfRequiredColumn]);
    }

    [Fact]
    public void Raw_sql_in_a_raw_string_with_several_lines_is_read_whole()
    {
        var findings = Check(b => b.Sql(
            """
            UPDATE release_identifiers ri
            SET library_id = s.library_id
            FROM scan_sessions s
            WHERE ri.learned_by_session_id = s.id;

            ALTER TABLE release_identifiers
              DROP COLUMN learned_by_session_id;
            """));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.DropsOrRenames]);
    }

    [Fact]
    public void Words_in_sql_comments_and_string_literals_are_not_statements()
    {
        Check(b => b.Sql(
            """
            -- ALTER TABLE items DROP COLUMN notes;
            /* ALTER TABLE items ALTER COLUMN notes SET NOT NULL; */
            UPDATE items SET notes = 'run ALTER TABLE items DROP COLUMN notes; later; it''s fine' WHERE id = 1;
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void A_statement_after_a_comment_with_a_semicolon_is_still_read()
    {
        var findings = Check(b => b.Sql(
            """
            -- tidy up; then
            ALTER TABLE items DROP COLUMN notes;
            """));

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.DropsOrRenames]);
    }

    // ---- A safe migration ----

    [Fact]
    public void A_migration_that_only_adds_what_the_previous_release_ignores_passes()
    {
        Check(b =>
        {
            b.CreateTable(
                name: "shelves",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table => table.PrimaryKey("pk_shelves", x => x.id));
            b.AddColumn<Guid>(name: "shelf_id", table: "items", type: "uuid", nullable: true);
            b.AddColumn<int>(name: "attempts", table: "jobs", type: "integer", nullable: false, defaultValue: 0);
            b.CreateIndex(name: "ix_items_shelf_id", table: "items", column: "shelf_id");
            b.AddForeignKey(name: "fk_items_shelves_shelf_id", table: "items", column: "shelf_id", principalTable: "shelves", principalColumn: "id");
            b.AlterColumn<string>(name: "title", table: "works", type: "text", nullable: true, oldClrType: typeof(string), oldType: "text", oldNullable: false);
            b.Sql("UPDATE items SET shelf_id = NULL;");
            b.Sql("ALTER TABLE jobs ADD COLUMN lease_id uuid NOT NULL DEFAULT gen_random_uuid();");
        }).ShouldBeEmpty();
    }

    [Fact]
    public void Every_problem_of_a_migration_is_reported_not_only_the_first()
    {
        var findings = Check(b =>
        {
            b.AddColumn<Guid>(name: "id", table: "release_identifiers", type: "uuid", nullable: false);
            b.DropColumn(name: "notes", table: "items");
            b.Sql("ALTER TABLE items ALTER COLUMN notes SET NOT NULL;");
        });

        findings.Select(f => f.Risk).ShouldBe([RollbackRisk.RequiredColumnWithoutDefault, RollbackRisk.DropsOrRenames, RollbackRisk.MakesColumnRequired]);
    }

    // ---- Waivers ----

    [Fact]
    public void A_waiver_accepts_the_risks_it_names_in_the_migration_it_names()
    {
        var findings = Check(b => b.DropColumn(name: "notes", table: "items"));
        RollbackWaiver[] waivers = [new("20990101000000_DropNotes", [RollbackRisk.DropsOrRenames], "Nothing reads items.notes since release 12.")];

        RollbackSafety.Unwaived("20990101000000_DropNotes", findings, waivers).ShouldBeEmpty();
    }

    [Fact]
    public void A_waiver_does_not_hide_a_risk_it_does_not_name()
    {
        var findings = Check(b =>
        {
            b.DropColumn(name: "notes", table: "items");
            b.AddColumn<Guid>(name: "id", table: "release_identifiers", type: "uuid", nullable: false);
        });
        RollbackWaiver[] waivers = [new("20990101000000_DropNotes", [RollbackRisk.DropsOrRenames], "Nothing reads items.notes since release 12.")];

        RollbackSafety.Unwaived("20990101000000_DropNotes", findings, waivers).Select(f => f.Risk).ShouldBe([RollbackRisk.RequiredColumnWithoutDefault]);
    }

    [Fact]
    public void A_waiver_does_not_cover_another_migration()
    {
        var findings = Check(b => b.DropColumn(name: "notes", table: "items"));
        RollbackWaiver[] waivers = [new("20990101000000_DropNotes", [RollbackRisk.DropsOrRenames], "Nothing reads items.notes since release 12.")];

        RollbackSafety.Unwaived("20990202000000_DropMore", findings, waivers).Select(f => f.Risk).ShouldBe([RollbackRisk.DropsOrRenames]);
    }

    // ---- Helpers ----

    private static List<RollbackFinding> Check(Action<MigrationBuilder> up)
    {
        var builder = new MigrationBuilder(Npgsql);
        up(builder);
        return [.. RollbackSafety.Check(builder.Operations)];
    }

    private static List<(string Id, IReadOnlyList<MigrationOperation> Up)> Migrations() =>
    [
        .. typeof(AppDbContext).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(Migration)) && !type.IsAbstract)
            .Select(type =>
            {
                var migration = (Migration)Activator.CreateInstance(type)!;
                migration.ActiveProvider = Npgsql;
                return (type.GetCustomAttribute<MigrationAttribute>()!.Id, migration.UpOperations);
            })
            .OrderBy(migration => migration.Id, StringComparer.Ordinal),
    ];
}
