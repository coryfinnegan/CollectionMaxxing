using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScanProvisionals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "provisional_id",
                table: "scan_candidates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_scan_snaps_library_id_id",
                table: "scan_snaps",
                columns: new[] { "library_id", "id" });

            migrationBuilder.CreateTable(
                name: "provisional_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snap_id = table.Column<Guid>(type: "uuid", nullable: true),
                    crop_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title_guess = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    media_hint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolve_action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisional_records", x => x.id);
                    table.UniqueConstraint("ak_provisional_records_library_id_id", x => new { x.library_id, x.id });
                    table.CheckConstraint("ck_provisional_records_media_hint", "\"media_hint\" in ('books', 'games', 'movies', 'mixed')");
                    table.CheckConstraint("ck_provisional_records_resolve_action", "\"resolve_action\" in ('promoted', 'resolved_to_existing')");
                    table.CheckConstraint("ck_provisional_records_resolved", "(resolved_at is null and resolve_action is null) or (resolved_at is not null and resolve_action is not null)");
                    table.CheckConstraint("ck_provisional_records_status", "\"status\" in ('unverified')");
                    table.ForeignKey(
                        name: "fk_provisional_records_assets_crop_asset_id",
                        column: x => x.crop_asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_provisional_records_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_provisional_records_locations_library_id_location_id",
                        columns: x => new { x.library_id, x.location_id },
                        principalTable: "locations",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_provisional_records_scan_candidates_library_id_candidate_id",
                        columns: x => new { x.library_id, x.candidate_id },
                        principalTable: "scan_candidates",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_provisional_records_scan_sessions_library_id_session_id",
                        columns: x => new { x.library_id, x.session_id },
                        principalTable: "scan_sessions",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_provisional_records_scan_snaps_library_id_snap_id",
                        columns: x => new { x.library_id, x.snap_id },
                        principalTable: "scan_snaps",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scan_candidates_library_id_provisional_id",
                table: "scan_candidates",
                columns: new[] { "library_id", "provisional_id" });

            migrationBuilder.CreateIndex(
                name: "ix_provisional_records_crop_asset_id",
                table: "provisional_records",
                column: "crop_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_provisional_records_library_id_candidate_id",
                table: "provisional_records",
                columns: new[] { "library_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_provisional_records_library_id_location_id",
                table: "provisional_records",
                columns: new[] { "library_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_provisional_records_library_id_resolved_at_created_at",
                table: "provisional_records",
                columns: new[] { "library_id", "resolved_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_provisional_records_library_id_session_id",
                table: "provisional_records",
                columns: new[] { "library_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_provisional_records_library_id_snap_id",
                table: "provisional_records",
                columns: new[] { "library_id", "snap_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_scan_candidates_provisional_records_library_id_provisional_",
                table: "scan_candidates",
                columns: new[] { "library_id", "provisional_id" },
                principalTable: "provisional_records",
                principalColumns: new[] { "library_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_scan_candidates_provisional_records_library_id_provisional_",
                table: "scan_candidates");

            migrationBuilder.DropTable(
                name: "provisional_records");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_scan_snaps_library_id_id",
                table: "scan_snaps");

            migrationBuilder.DropIndex(
                name: "ix_scan_candidates_library_id_provisional_id",
                table: "scan_candidates");

            migrationBuilder.DropColumn(
                name: "provisional_id",
                table: "scan_candidates");
        }
    }
}
