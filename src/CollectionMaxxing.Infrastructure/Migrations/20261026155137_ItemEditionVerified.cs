using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ItemEditionVerified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default is the backfill: a non-nullable column with a default writes it into every
            // pre-existing row, so nothing already in a library ever shows as edition-unverified (issue #343).
            migrationBuilder.AddColumn<bool>(
                name: "edition_verified",
                table: "items",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_library_id_edition_verified",
                table: "items",
                columns: new[] { "library_id", "edition_verified" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_items_library_id_edition_verified",
                table: "items");

            migrationBuilder.DropColumn(
                name: "edition_verified",
                table: "items");
        }
    }
}
