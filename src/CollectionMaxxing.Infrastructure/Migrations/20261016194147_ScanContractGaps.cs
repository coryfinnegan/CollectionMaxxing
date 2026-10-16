using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanContractGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "change_seq",
                table: "scan_candidates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "edited_edition",
                table: "scan_candidates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_format",
                table: "scan_candidates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_platform",
                table: "scan_candidates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "user_edited",
                table: "scan_candidates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_scan_candidates_session_id_change_seq",
                table: "scan_candidates",
                columns: new[] { "session_id", "change_seq" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_edited_format",
                table: "scan_candidates",
                sql: "\"edited_format\" in ('hardcover', 'paperback', 'mass_market', 'vhs', 'betamax', 'laserdisc', 'dvd', 'bluray', 'uhd_bluray', 'game_cartridge', 'game_disc', 'game_card', 'other')");

            // Hand-written (SCAN-1d): the per-session change counter behind scan_candidates.change_seq. The counter row is
            // locked by the first candidate write of a transaction until it commits, so values are handed out in commit order and
            // a poller that has seen value N can never miss a lower one that commits later. Existing rows start at their seq.
            migrationBuilder.Sql(
                """
                create table scan_session_change_counters (
                    session_id uuid not null primary key references scan_sessions (id) on delete cascade,
                    counter integer not null
                );

                update scan_candidates set change_seq = seq;

                insert into scan_session_change_counters (session_id, counter)
                select session_id, max(seq) from scan_candidates group by session_id;

                create function scan_candidates_set_change_seq() returns trigger language plpgsql as $$
                begin
                    insert into scan_session_change_counters as c (session_id, counter) values (new.session_id, 1)
                    on conflict (session_id) do update set counter = c.counter + 1
                    returning c.counter into new.change_seq;
                    return new;
                end
                $$;

                create trigger trg_scan_candidates_change_seq
                    before insert or update on scan_candidates
                    for each row execute function scan_candidates_set_change_seq();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger trg_scan_candidates_change_seq on scan_candidates;
                drop function scan_candidates_set_change_seq();
                drop table scan_session_change_counters;
                """);

            migrationBuilder.DropIndex(
                name: "ix_scan_candidates_session_id_change_seq",
                table: "scan_candidates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_edited_format",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "change_seq",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "edited_edition",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "edited_format",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "edited_platform",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "user_edited",
                table: "scan_candidates");
        }
    }
}
