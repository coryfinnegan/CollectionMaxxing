using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanAutoCommit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_commit_copies",
                table: "scan_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Hand-adjusted: the column arrives nullable so existing sessions can be backfilled first (an empty
            // array would turn auto-add off for them). A session started before this column existed auto-adds T0.
            migrationBuilder.AddColumn<string[]>(
                name: "auto_commit_tiers",
                table: "scan_sessions",
                type: "text[]",
                nullable: true);

            migrationBuilder.Sql("update scan_sessions set auto_commit_tiers = '{T0}';");

            migrationBuilder.AlterColumn<string[]>(
                name: "auto_commit_tiers",
                table: "scan_sessions",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<Guid[]>(
                name: "auto_committed_item_ids",
                table: "scan_sessions",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<Guid[]>(
                name: "auto_committed_item_ids",
                table: "scan_candidates",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tier",
                table: "scan_candidates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_scan_candidates_tier",
                table: "scan_candidates",
                sql: "\"tier\" in ('T0', 'T1', 'T2')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scan_candidates_tier",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "auto_commit_copies",
                table: "scan_sessions");

            migrationBuilder.DropColumn(
                name: "auto_commit_tiers",
                table: "scan_sessions");

            migrationBuilder.DropColumn(
                name: "auto_committed_item_ids",
                table: "scan_sessions");

            migrationBuilder.DropColumn(
                name: "auto_committed_item_ids",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "tier",
                table: "scan_candidates");
        }
    }
}
