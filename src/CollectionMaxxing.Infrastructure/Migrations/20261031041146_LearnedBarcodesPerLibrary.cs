using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <summary>
    /// A barcode a person taught by picking a release by hand belongs to the library that taught it; a provider's link stays
    /// shared. Adds <c>library_id</c> (null = shared) and a key of the row's own, because the same code may now be learned for the
    /// same release by several libraries and a nullable column cannot be part of a key. Uniqueness moves to two partial indexes.
    /// <para>
    /// Backfill: a link with a <c>learned_by_session_id</c> was taught by that commit, so it takes the session's library
    /// (the session row always exists while the column is set: the foreign key clears the column when a session is deleted).
    /// Every other link stays shared. A link whose session is already gone looks exactly like a provider's, and dropping
    /// provider links is never safe, so those stay shared.
    /// </para>
    /// <para>
    /// Down restores the old key. Two libraries' links for the same barcode and release (or a learned one beside a shared one)
    /// would collide, so one is kept: the shared one if there is one, otherwise the lowest id. Learned links become shared again.
    /// </para>
    /// </summary>
    public partial class LearnedBarcodesPerLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The new key. Every existing row gets its own value; the default is only there to fill them in.
            migrationBuilder.Sql("ALTER TABLE release_identifiers ADD COLUMN id uuid NOT NULL DEFAULT gen_random_uuid();");

            migrationBuilder.DropPrimaryKey(
                name: "pk_release_identifiers",
                table: "release_identifiers");

            migrationBuilder.AddPrimaryKey(
                name: "pk_release_identifiers",
                table: "release_identifiers",
                column: "id");

            migrationBuilder.Sql("ALTER TABLE release_identifiers ALTER COLUMN id DROP DEFAULT;");

            migrationBuilder.AddColumn<Guid>(
                name: "library_id",
                table: "release_identifiers",
                type: "uuid",
                nullable: true);

            // What a commit taught is its library's.
            migrationBuilder.Sql(
                """
                UPDATE release_identifiers ri
                SET library_id = s.library_id
                FROM scan_sessions s
                WHERE ri.learned_by_session_id = s.id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_release_identifiers_library_id",
                table: "release_identifiers",
                column: "library_id",
                filter: "library_id is not null");

            migrationBuilder.CreateIndex(
                name: "ux_release_identifiers_learned",
                table: "release_identifiers",
                columns: new[] { "type", "value", "release_id", "library_id" },
                unique: true,
                filter: "library_id is not null");

            migrationBuilder.CreateIndex(
                name: "ux_release_identifiers_shared",
                table: "release_identifiers",
                columns: new[] { "type", "value", "release_id" },
                unique: true,
                filter: "library_id is null");

            migrationBuilder.AddForeignKey(
                name: "fk_release_identifiers_libraries_library_id",
                table: "release_identifiers",
                column: "library_id",
                principalTable: "libraries",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old key allows one row per (type, value, release): keep the shared link, else the lowest id.
            migrationBuilder.Sql(
                """
                DELETE FROM release_identifiers a
                USING release_identifiers b
                WHERE a.type = b.type AND a.value = b.value AND a.release_id = b.release_id AND a.id <> b.id
                  AND a.library_id IS NOT NULL
                  AND (b.library_id IS NULL OR b.id < a.id);
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_release_identifiers_libraries_library_id",
                table: "release_identifiers");

            migrationBuilder.DropPrimaryKey(
                name: "pk_release_identifiers",
                table: "release_identifiers");

            migrationBuilder.DropIndex(
                name: "ix_release_identifiers_library_id",
                table: "release_identifiers");

            migrationBuilder.DropIndex(
                name: "ux_release_identifiers_learned",
                table: "release_identifiers");

            migrationBuilder.DropIndex(
                name: "ux_release_identifiers_shared",
                table: "release_identifiers");

            migrationBuilder.DropColumn(
                name: "id",
                table: "release_identifiers");

            migrationBuilder.DropColumn(
                name: "library_id",
                table: "release_identifiers");

            migrationBuilder.AddPrimaryKey(
                name: "pk_release_identifiers",
                table: "release_identifiers",
                columns: new[] { "type", "value", "release_id" });
        }
    }
}
