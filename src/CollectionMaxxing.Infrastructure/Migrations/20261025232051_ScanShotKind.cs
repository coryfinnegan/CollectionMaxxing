using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanShotKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "shot_kind",
                table: "scan_snaps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "segment_id",
                table: "scan_candidates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shot_kind",
                table: "scan_candidates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_snaps_shot_kind",
                table: "scan_snaps",
                sql: "\"shot_kind\" in ('barcode', 'single_cover', 'shelf_segment', 'loose_item', 'unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_shot_kind",
                table: "scan_candidates",
                sql: "\"shot_kind\" in ('barcode', 'single_cover', 'shelf_segment', 'loose_item', 'unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_snaps_shot_kind",
                table: "scan_snaps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_shot_kind",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "shot_kind",
                table: "scan_snaps");

            migrationBuilder.DropColumn(
                name: "segment_id",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "shot_kind",
                table: "scan_candidates");
        }
    }
}
