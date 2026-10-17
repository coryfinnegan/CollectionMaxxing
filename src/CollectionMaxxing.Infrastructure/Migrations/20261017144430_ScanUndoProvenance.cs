using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanUndoProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "committed_item_ids",
                table: "scan_sessions",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_session_id",
                table: "releases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "learned_by_session_id",
                table: "release_identifiers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "item_tombstones",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_tombstones", x => x.item_id);
                    table.ForeignKey(
                        name: "fk_item_tombstones_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_releases_created_by_session_id",
                table: "releases",
                column: "created_by_session_id",
                filter: "created_by_session_id is not null");

            migrationBuilder.CreateIndex(
                name: "ix_release_identifiers_learned_by_session_id",
                table: "release_identifiers",
                column: "learned_by_session_id",
                filter: "learned_by_session_id is not null");

            migrationBuilder.CreateIndex(
                name: "ix_item_tombstones_library_id_deleted_at_item_id",
                table: "item_tombstones",
                columns: new[] { "library_id", "deleted_at", "item_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_release_identifiers_scan_sessions_learned_by_session_id",
                table: "release_identifiers",
                column: "learned_by_session_id",
                principalTable: "scan_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_releases_scan_sessions_created_by_session_id",
                table: "releases",
                column: "created_by_session_id",
                principalTable: "scan_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_release_identifiers_scan_sessions_learned_by_session_id",
                table: "release_identifiers");

            migrationBuilder.DropForeignKey(
                name: "fk_releases_scan_sessions_created_by_session_id",
                table: "releases");

            migrationBuilder.DropTable(
                name: "item_tombstones");

            migrationBuilder.DropIndex(
                name: "ix_releases_created_by_session_id",
                table: "releases");

            migrationBuilder.DropIndex(
                name: "ix_release_identifiers_learned_by_session_id",
                table: "release_identifiers");

            migrationBuilder.DropColumn(
                name: "committed_item_ids",
                table: "scan_sessions");

            migrationBuilder.DropColumn(
                name: "created_by_session_id",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "learned_by_session_id",
                table: "release_identifiers");
        }
    }
}
