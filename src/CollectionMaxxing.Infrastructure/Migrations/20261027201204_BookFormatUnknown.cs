using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BookFormatUnknown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_edited_format",
                table: "scan_candidates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_releases_format",
                table: "releases");

            // Hand-adjusted (#444): every global provider-sourced 'other' predates the Unknown value, and the
            // pre-fix code could only produce 'other' from missing data (no provider ever returned Other; every
            // default was `?? Other`). Re-label those rows honestly. User and private releases keep their value:
            // a person may have picked Other deliberately.
            migrationBuilder.Sql(
                "update releases set format = 'unknown' where format = 'other' and source = 'provider' and owner_library_id is null;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_edited_format",
                table: "scan_candidates",
                sql: "\"edited_format\" in ('hardcover', 'paperback', 'mass_market', 'board_book', 'spiral_bound', 'vhs', 'betamax', 'laserdisc', 'dvd', 'bluray', 'uhd_bluray', 'game_cartridge', 'game_disc', 'game_card', 'other', 'unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_releases_format",
                table: "releases",
                sql: "\"format\" in ('hardcover', 'paperback', 'mass_market', 'board_book', 'spiral_bound', 'vhs', 'betamax', 'laserdisc', 'dvd', 'bluray', 'uhd_bluray', 'game_cartridge', 'game_disc', 'game_card', 'other', 'unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_edited_format",
                table: "scan_candidates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_releases_format",
                table: "releases");

            // Hand-adjusted (#444): fold the new values back so the old constraint can be re-added. Lossy, as any
            // enum-narrowing rollback is.
            migrationBuilder.Sql("update releases set format = 'other' where format in ('board_book', 'spiral_bound', 'unknown');");
            migrationBuilder.Sql("update scan_candidates set edited_format = 'other' where edited_format in ('board_book', 'spiral_bound', 'unknown');");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_edited_format",
                table: "scan_candidates",
                sql: "\"edited_format\" in ('hardcover', 'paperback', 'mass_market', 'vhs', 'betamax', 'laserdisc', 'dvd', 'bluray', 'uhd_bluray', 'game_cartridge', 'game_disc', 'game_card', 'other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_releases_format",
                table: "releases",
                sql: "\"format\" in ('hardcover', 'paperback', 'mass_market', 'vhs', 'betamax', 'laserdisc', 'dvd', 'bluray', 'uhd_bluray', 'game_cartridge', 'game_disc', 'game_card', 'other')");
        }
    }
}
