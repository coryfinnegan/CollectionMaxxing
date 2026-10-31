using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanSessionUtcOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "utc_offset_minutes",
                table: "scan_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_sessions_utc_offset_minutes",
                table: "scan_sessions",
                sql: "utc_offset_minutes is null or utc_offset_minutes between -720 and 840");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_sessions_utc_offset_minutes",
                table: "scan_sessions");

            migrationBuilder.DropColumn(
                name: "utc_offset_minutes",
                table: "scan_sessions");
        }
    }
}
