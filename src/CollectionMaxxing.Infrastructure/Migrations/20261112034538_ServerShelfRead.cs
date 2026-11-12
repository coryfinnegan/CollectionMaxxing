using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ServerShelfRead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_source",
                table: "scan_candidates");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "server_read_at",
                table: "scan_snaps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_source",
                table: "scan_candidates",
                sql: "\"source\" in ('barcode', 'picture_apple_model', 'picture_text', 'search', 'picture_server_model')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_source",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "server_read_at",
                table: "scan_snaps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_source",
                table: "scan_candidates",
                sql: "\"source\" in ('barcode', 'picture_apple_model', 'picture_text', 'search')");
        }
    }
}
