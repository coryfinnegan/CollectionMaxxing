using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingAccountsAndInviteCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // There is no setup code any more (ADR-027), and a recovery code now names its account: the codes still waiting (24 hours
            // and 30 minutes at most) can't say whose they are, so they go. A new recovery code is one command away.
            migrationBuilder.Sql("DELETE FROM setup_codes;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_setup_codes_purpose",
                table: "setup_codes");

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "setup_codes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "code_hash",
                table: "library_invites",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_setup_codes_user_id",
                table: "setup_codes",
                column: "user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_setup_codes_purpose",
                table: "setup_codes",
                sql: "\"purpose\" in ('recovery')");

            migrationBuilder.CreateIndex(
                name: "ix_library_invites_code_hash",
                table: "library_invites",
                column: "code_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_setup_codes_users_user_id",
                table: "setup_codes",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_setup_codes_users_user_id",
                table: "setup_codes");

            migrationBuilder.DropIndex(
                name: "ix_setup_codes_user_id",
                table: "setup_codes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_setup_codes_purpose",
                table: "setup_codes");

            migrationBuilder.DropIndex(
                name: "ix_library_invites_code_hash",
                table: "library_invites");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "setup_codes");

            migrationBuilder.DropColumn(
                name: "code_hash",
                table: "library_invites");

            migrationBuilder.AddCheckConstraint(
                name: "ck_setup_codes_purpose",
                table: "setup_codes",
                sql: "\"purpose\" in ('setup', 'recovery')");
        }
    }
}
