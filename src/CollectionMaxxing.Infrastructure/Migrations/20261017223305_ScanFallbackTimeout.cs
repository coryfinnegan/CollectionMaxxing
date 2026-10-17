using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanFallbackTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps",
                sql: "\"fallback_reason\" in ('unavailable', 'declined', 'context', 'timeout')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_snaps_fallback_reason",
                table: "scan_snaps",
                sql: "\"fallback_reason\" in ('unavailable', 'declined', 'context')");
        }
    }
}
