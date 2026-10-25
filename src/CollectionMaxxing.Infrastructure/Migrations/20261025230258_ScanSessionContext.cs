using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanSessionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-adjusted: the columns arrive nullable so existing sessions can be backfilled first (an empty string
            // would violate the check constraints below). A session started before this column existed is a mixed scan at home.
            migrationBuilder.AddColumn<string>(
                name: "channel",
                table: "scan_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "media_hint",
                table: "scan_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("update scan_sessions set channel = 'home', media_hint = 'mixed';");

            migrationBuilder.AlterColumn<string>(
                name: "channel",
                table: "scan_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false);

            migrationBuilder.AlterColumn<string>(
                name: "media_hint",
                table: "scan_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_sessions_channel",
                table: "scan_sessions",
                sql: "\"channel\" in ('home', 'in_store')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_sessions_media_hint",
                table: "scan_sessions",
                sql: "\"media_hint\" in ('books', 'games', 'movies', 'mixed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_sessions_channel",
                table: "scan_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_sessions_media_hint",
                table: "scan_sessions");

            migrationBuilder.DropColumn(
                name: "channel",
                table: "scan_sessions");

            migrationBuilder.DropColumn(
                name: "media_hint",
                table: "scan_sessions");
        }
    }
}
