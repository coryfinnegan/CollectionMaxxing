using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanEngineFmText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_snaps_engine",
                table: "scan_snaps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_snaps_engine",
                table: "scan_snaps",
                sql: "\"engine\" in ('apple_model', 'vision_pipeline', 'fm_text')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_snaps_engine",
                table: "scan_snaps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_snaps_engine",
                table: "scan_snaps",
                sql: "\"engine\" in ('apple_model', 'vision_pipeline')");
        }
    }
}
