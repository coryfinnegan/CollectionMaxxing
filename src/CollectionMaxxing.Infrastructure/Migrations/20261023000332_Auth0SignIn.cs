using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Auth0SignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_pairings");

            migrationBuilder.DropTable(
                name: "openiddict_scopes");

            migrationBuilder.DropTable(
                name: "openiddict_tokens");

            migrationBuilder.DropTable(
                name: "user_passkeys");

            migrationBuilder.DropTable(
                name: "openiddict_authorizations");

            migrationBuilder.DropTable(
                name: "openiddict_applications");

            migrationBuilder.DropPrimaryKey(
                name: "pk_user_logins",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "login_provider",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "provider_key",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "provider_display_name",
                table: "user_logins");

            migrationBuilder.AddColumn<Guid>(
                name: "id",
                table: "user_logins",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "user_logins",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "user_logins",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_used_at",
                table: "user_logins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "user_logins",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "user_logins",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "pk_user_logins",
                table: "user_logins",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_subject",
                table: "user_logins",
                column: "subject",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_logins_provider",
                table: "user_logins",
                sql: "provider = 'auth0'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_user_logins",
                table: "user_logins");

            migrationBuilder.DropIndex(
                name: "ix_user_logins_subject",
                table: "user_logins");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_logins_provider",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "id",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "email",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "last_used_at",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "user_logins");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "user_logins");

            migrationBuilder.AddColumn<string>(
                name: "login_provider",
                table: "user_logins",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "provider_key",
                table: "user_logins",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "provider_display_name",
                table: "user_logins",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_user_logins",
                table: "user_logins",
                columns: new[] { "login_provider", "provider_key" });

            migrationBuilder.CreateTable(
                name: "device_pairings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_pairings", x => x.id);
                    table.CheckConstraint("ck_device_pairings_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_device_pairings_expiry", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_device_pairings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "text", nullable: true),
                    client_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    json_web_key_set = table.Column<string>(type: "text", nullable: true),
                    permissions = table.Column<string>(type: "text", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redirect_uris = table.Column<string>(type: "text", nullable: true),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    descriptions = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_scopes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_passkeys",
                columns: table => new
                {
                    credential_id = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_passkeys", x => x.credential_id);
                    table.ForeignKey(
                        name: "fk_user_passkeys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "fk_openiddict_authorizations_openiddict_applications_applicati",
                        column: x => x.application_id,
                        principalTable: "openiddict_applications",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "openiddict_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redemption_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_openiddict_tokens_openiddict_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "openiddict_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_openiddict_tokens_openiddict_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalTable: "openiddict_authorizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_pairings_token_hash",
                table: "device_pairings",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_pairings_user_id_expires_at",
                table: "device_pairings",
                columns: new[] { "user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_applications_client_id",
                table: "openiddict_applications",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_authorizations_application_id_status_subject_type",
                table: "openiddict_authorizations",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_scopes_name",
                table: "openiddict_scopes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_tokens_application_id_status_subject_type",
                table: "openiddict_tokens",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_tokens_authorization_id",
                table: "openiddict_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_tokens_reference_id",
                table: "openiddict_tokens",
                column: "reference_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_passkeys_user_id",
                table: "user_passkeys",
                column: "user_id");
        }
    }
}
