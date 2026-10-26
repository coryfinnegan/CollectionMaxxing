using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanGuidanceCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps");

            migrationBuilder.AddColumn<string>(
                name: "guidance_code",
                table: "scan_candidates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps",
                sql: "\"fallback_reason\" in ('unavailable', 'declined', 'context', 'timeout', 'move_closer', 'split_segment', 'hold_steady', 'kill_glare')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_guidance_code",
                table: "scan_candidates",
                sql: "\"guidance_code\" in ('move_closer', 'split_segment', 'hold_steady', 'kill_glare', 'shoot_cover', 'pick_match', 'scan_barcode')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_guidance_code",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "guidance_code",
                table: "scan_candidates");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps",
                sql: "\"fallback_reason\" in ('unavailable', 'declined', 'context', 'timeout')");
        }
    }
}
