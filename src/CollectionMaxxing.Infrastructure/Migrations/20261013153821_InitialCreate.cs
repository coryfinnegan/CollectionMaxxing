using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_jobs_status", "\"status\" in ('pending', 'running', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "libraries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_libraries", x => x.id);
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
                name: "platforms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    short_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    igdb_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platforms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_cache",
                columns: table => new
                {
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_cache", x => new { x.provider, x.key });
                });

            migrationBuilder.CreateTable(
                name: "provider_quota",
                columns: table => new
                {
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    used = table.Column<int>(type: "integer", nullable: false),
                    limit = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_quota", x => new { x.provider, x.day });
                    table.CheckConstraint("ck_provider_quota_limit", "\"limit\" >= 0");
                    table.CheckConstraint("ck_provider_quota_used", "used >= 0");
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "setup_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setup_codes", x => x.id);
                    table.CheckConstraint("ck_setup_codes_purpose", "\"purpose\" in ('setup', 'recovery')");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    blob_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    owner_library_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.CheckConstraint("ck_assets_kind", "\"kind\" in ('cover', 'scan_photo', 'user_photo')");
                    table.ForeignKey(
                        name: "fk_assets_libraries_owner_library_id",
                        column: x => x.owner_library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_locations", x => x.id);
                    table.UniqueConstraint("ak_locations_library_id_id", x => new { x.library_id, x.id });
                    table.ForeignKey(
                        name: "fk_locations_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_locations_locations_library_id_parent_id",
                        columns: x => new { x.library_id, x.parent_id },
                        principalTable: "locations",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "citext", maxLength: 50, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                    table.UniqueConstraint("ak_tags_library_id_id", x => new { x.library_id, x.id });
                    table.ForeignKey(
                        name: "fk_tags_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
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
                name: "role_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_claims_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    library_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "device_pairings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    device_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    authorization_id = table.Column<Guid>(type: "uuid", nullable: true)
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
                name: "idempotency_keys",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.user_id, x.key });
                    table.ForeignKey(
                        name: "fk_idempotency_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "library_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_invites", x => x.id);
                    table.CheckConstraint("ck_library_invites_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_library_invites_role", "role in ('editor', 'viewer')");
                    table.ForeignKey(
                        name: "fk_library_invites_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_library_invites_users_accepted_by",
                        column: x => x.accepted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_library_invites_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "library_members",
                columns: table => new
                {
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_members", x => new { x.library_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_library_members_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_library_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_claims_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "works",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sort_title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    normalized_title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    original_year = table.Column<int>(type: "integer", nullable: true),
                    creators = table.Column<string>(type: "jsonb", nullable: false),
                    genres = table.Column<string[]>(type: "text[]", nullable: false),
                    synopsis = table.Column<string>(type: "text", nullable: true),
                    external_ids = table.Column<string>(type: "jsonb", nullable: false),
                    cover_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_works", x => x.id);
                    table.CheckConstraint("ck_works_kind", "\"kind\" in ('book', 'film', 'tv', 'game', 'other')");
                    table.ForeignKey(
                        name: "fk_works_assets_cover_asset_id",
                        column: x => x.cover_asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    default_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_sessions", x => x.id);
                    table.UniqueConstraint("ak_scan_sessions_library_id_id", x => new { x.library_id, x.id });
                    table.CheckConstraint("ck_scan_sessions_default_status", "\"default_status\" in ('owned', 'wishlist', 'lent', 'sold', 'missing')");
                    table.CheckConstraint("ck_scan_sessions_status", "\"status\" in ('open', 'committed', 'abandoned')");
                    table.ForeignKey(
                        name: "fk_scan_sessions_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scan_sessions_locations_library_id_default_location_id",
                        columns: x => new { x.library_id, x.default_location_id },
                        principalTable: "locations",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.CreateTable(
                name: "releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    platform_id = table.Column<Guid>(type: "uuid", nullable: true),
                    region = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    edition = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    release_date = table.Column<DateOnly>(type: "date", nullable: true),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    runtime_minutes = table.Column<int>(type: "integer", nullable: true),
                    cover_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    owner_library_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_payloads = table.Column<string>(type: "jsonb", nullable: true),
                    refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_releases", x => x.id);
                    table.CheckConstraint("ck_releases_format", "\"format\" in ('hardcover', 'paperback', 'mass_market', 'vhs', 'betamax', 'laserdisc', 'dvd', 'bluray', 'uhd_bluray', 'game_cartridge', 'game_disc', 'game_card', 'other')");
                    table.CheckConstraint("ck_releases_region", "\"region\" in ('ntsc_u', 'pal', 'ntsc_j', 'region_free')");
                    table.CheckConstraint("ck_releases_source", "\"source\" in ('provider', 'user', 'ai')");
                    table.ForeignKey(
                        name: "fk_releases_assets_cover_asset_id",
                        column: x => x.cover_asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_releases_libraries_owner_library_id",
                        column: x => x.owner_library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_releases_platforms_platform_id",
                        column: x => x.platform_id,
                        principalTable: "platforms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_releases_works_work_id",
                        column: x => x.work_id,
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_snaps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    engine = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fell_back = table.Column<bool>(type: "boolean", nullable: false),
                    fallback_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_snaps", x => x.id);
                    table.UniqueConstraint("ak_scan_snaps_session_id_id", x => new { x.session_id, x.id });
                    table.CheckConstraint("ck_scan_snaps_engine", "\"engine\" in ('apple_model', 'vision_pipeline')");
                    table.CheckConstraint("ck_scan_snaps_fallback_reason", "\"fallback_reason\" in ('unavailable', 'declined', 'context')");
                    table.CheckConstraint("ck_scan_snaps_fell_back_has_reason", "fell_back = (fallback_reason is not null)");
                    table.CheckConstraint("ck_scan_snaps_item_count", "item_count >= 0");
                    table.CheckConstraint("ck_scan_snaps_trigger", "\"trigger\" in ('auto', 'shutter', 'import')");
                    table.ForeignKey(
                        name: "fk_scan_snaps_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_snaps_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scan_snaps_scan_sessions_library_id_session_id",
                        columns: x => new { x.library_id, x.session_id },
                        principalTable: "scan_sessions",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    completeness = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    acquired_on = table.Column<DateOnly>(type: "date", nullable: true),
                    lent_to = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lent_on = table.Column<DateOnly>(type: "date", nullable: true),
                    custom_title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    custom_cover_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cover_choice = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    added_via = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.UniqueConstraint("ak_items_library_id_id", x => new { x.library_id, x.id });
                    table.CheckConstraint("ck_items_added_via", "\"added_via\" in ('barcode', 'picture', 'search', 'import', 'manual')");
                    table.CheckConstraint("ck_items_completeness", "\"completeness\" in ('complete', 'disc_only', 'cart_only', 'box_only', 'no_manual', 'n_a')");
                    table.CheckConstraint("ck_items_condition", "\"condition\" in ('sealed', 'mint', 'very_good', 'good', 'fair', 'poor')");
                    table.CheckConstraint("ck_items_cover_choice", "\"cover_choice\" in ('release', 'my_photo')");
                    table.CheckConstraint("ck_items_status", "\"status\" in ('owned', 'wishlist', 'lent', 'sold', 'missing')");
                    table.ForeignKey(
                        name: "fk_items_assets_custom_cover_asset_id",
                        column: x => x.custom_cover_asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_items_locations_library_id_location_id",
                        columns: x => new { x.library_id, x.location_id },
                        principalTable: "locations",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_identifiers",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_release_identifiers", x => new { x.type, x.value, x.release_id });
                    table.CheckConstraint("ck_release_identifiers_type", "\"type\" in ('isbn13', 'gtin13', 'gtin8', 'asin')");
                    table.ForeignKey(
                        name: "fk_release_identifiers_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_feature_prints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_feature_prints", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_feature_prints_items_library_id_item_id",
                        columns: x => new { x.library_id, x.item_id },
                        principalTable: "items",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_feature_prints_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_feature_prints_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "item_tags",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_tags", x => new { x.item_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_item_tags_items_library_id_item_id",
                        columns: x => new { x.library_id, x.item_id },
                        principalTable: "items",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_tags_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_tags_tags_library_id_tag_id",
                        columns: x => new { x.library_id, x.tag_id },
                        principalTable: "tags",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scan_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    raw_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    symbology = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    visible_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    snap_id = table.Column<Guid>(type: "uuid", nullable: true),
                    local_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    box_x = table.Column<double>(type: "double precision", nullable: true),
                    box_y = table.Column<double>(type: "double precision", nullable: true),
                    box_w = table.Column<double>(type: "double precision", nullable: true),
                    box_h = table.Column<double>(type: "double precision", nullable: true),
                    box_source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resolution = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    matched_release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    score = table.Column<double>(type: "double precision", nullable: true),
                    art_distance = table.Column<double>(type: "double precision", nullable: true),
                    inferred = table.Column<string>(type: "jsonb", nullable: true),
                    alternatives = table.Column<string>(type: "jsonb", nullable: true),
                    trace = table.Column<string>(type: "jsonb", nullable: true),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    copies = table.Column<int>(type: "integer", nullable: false),
                    already_owned_count = table.Column<int>(type: "integer", nullable: false),
                    created_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_candidates", x => x.id);
                    table.UniqueConstraint("ak_scan_candidates_library_id_id", x => new { x.library_id, x.id });
                    table.CheckConstraint("ck_scan_candidates_already_owned_count", "already_owned_count >= 0");
                    table.CheckConstraint("ck_scan_candidates_art_distance", "art_distance >= 0");
                    table.CheckConstraint("ck_scan_candidates_box", "(box_x is null and box_y is null and box_w is null and box_h is null) or (box_x is not null and box_y is not null and box_w is not null and box_h is not null and box_x between 0 and 1 and box_y between 0 and 1 and box_w between 0 and 1 and box_h between 0 and 1)");
                    table.CheckConstraint("ck_scan_candidates_box_source", "\"box_source\" in ('ocr', 'rect')");
                    table.CheckConstraint("ck_scan_candidates_copies", "copies >= 1");
                    table.CheckConstraint("ck_scan_candidates_decision", "\"decision\" in ('undecided', 'accepted', 'rejected')");
                    table.CheckConstraint("ck_scan_candidates_resolution", "\"resolution\" in ('pending', 'matched', 'ambiguous', 'not_found', 'error')");
                    table.CheckConstraint("ck_scan_candidates_score", "score between 0 and 1");
                    table.CheckConstraint("ck_scan_candidates_source", "\"source\" in ('barcode', 'picture_apple_model', 'picture_text', 'search')");
                    table.ForeignKey(
                        name: "fk_scan_candidates_items_library_id_created_item_id",
                        columns: x => new { x.library_id, x.created_item_id },
                        principalTable: "items",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_candidates_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scan_candidates_releases_matched_release_id",
                        column: x => x.matched_release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_candidates_scan_sessions_library_id_session_id",
                        columns: x => new { x.library_id, x.session_id },
                        principalTable: "scan_sessions",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scan_candidates_scan_snaps_session_id_snap_id",
                        columns: x => new { x.session_id, x.snap_id },
                        principalTable: "scan_snaps",
                        principalColumns: new[] { "session_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "match_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ranked_first_release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    picked_release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_feedback", x => x.id);
                    table.ForeignKey(
                        name: "fk_match_feedback_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_match_feedback_scan_candidates_library_id_candidate_id",
                        columns: x => new { x.library_id, x.candidate_id },
                        principalTable: "scan_candidates",
                        principalColumns: new[] { "library_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_owner_library_id_sha256",
                table: "assets",
                columns: new[] { "owner_library_id", "sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_user_id",
                table: "audit_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_at",
                table: "audit_events",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_entity_type_entity_id",
                table: "audit_events",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_library_id_at",
                table: "audit_events",
                columns: new[] { "library_id", "at" },
                descending: new[] { false, true });

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
                name: "ix_idempotency_keys_expires_at",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_item_feature_prints_item_id_release_id_revision",
                table: "item_feature_prints",
                columns: new[] { "item_id", "release_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_feature_prints_library_id_created_at",
                table: "item_feature_prints",
                columns: new[] { "library_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_item_feature_prints_library_id_item_id",
                table: "item_feature_prints",
                columns: new[] { "library_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_item_feature_prints_release_id",
                table: "item_feature_prints",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_tags_library_id_item_id",
                table: "item_tags",
                columns: new[] { "library_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_item_tags_library_id_tag_id",
                table: "item_tags",
                columns: new[] { "library_id", "tag_id" });

            migrationBuilder.CreateIndex(
                name: "ix_items_created_by",
                table: "items",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_items_custom_cover_asset_id",
                table: "items",
                column: "custom_cover_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_custom_title_trgm",
                table: "items",
                column: "custom_title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_items_library_id_created_at",
                table: "items",
                columns: new[] { "library_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_items_library_id_location_id",
                table: "items",
                columns: new[] { "library_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_items_library_id_release_id",
                table: "items",
                columns: new[] { "library_id", "release_id" });

            migrationBuilder.CreateIndex(
                name: "ix_items_library_id_status",
                table: "items",
                columns: new[] { "library_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_items_release_id",
                table: "items",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_run_after",
                table: "jobs",
                columns: new[] { "status", "run_after" });

            migrationBuilder.CreateIndex(
                name: "ix_library_invites_accepted_by",
                table: "library_invites",
                column: "accepted_by");

            migrationBuilder.CreateIndex(
                name: "ix_library_invites_created_by",
                table: "library_invites",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_library_invites_library_id_expires_at",
                table: "library_invites",
                columns: new[] { "library_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_library_invites_token_hash",
                table: "library_invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_library_members_user_id",
                table: "library_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_library_members_one_owner",
                table: "library_members",
                column: "library_id",
                unique: true,
                filter: "role = 'owner'");

            migrationBuilder.CreateIndex(
                name: "ix_locations_library_id_parent_id_sort_order",
                table: "locations",
                columns: new[] { "library_id", "parent_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_locations_name_per_parent",
                table: "locations",
                columns: new[] { "library_id", "parent_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_match_feedback_candidate_id",
                table: "match_feedback",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_match_feedback_library_id_candidate_id",
                table: "match_feedback",
                columns: new[] { "library_id", "candidate_id" });

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
                name: "ix_platforms_igdb_id",
                table: "platforms",
                column: "igdb_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platforms_name",
                table: "platforms",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_cache_expires_at",
                table: "provider_cache",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_release_identifiers_release_id",
                table: "release_identifiers",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_release_identifiers_type_value",
                table: "release_identifiers",
                columns: new[] { "type", "value" });

            migrationBuilder.CreateIndex(
                name: "ix_releases_cover_asset_id",
                table: "releases",
                column: "cover_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_releases_owner_library_id",
                table: "releases",
                column: "owner_library_id",
                filter: "owner_library_id is not null");

            migrationBuilder.CreateIndex(
                name: "ix_releases_platform_id",
                table: "releases",
                column: "platform_id");

            migrationBuilder.CreateIndex(
                name: "ix_releases_work_id",
                table: "releases",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_claims_role_id",
                table: "role_claims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_roles_normalized_name",
                table: "roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scan_candidates_library_id_created_item_id",
                table: "scan_candidates",
                columns: new[] { "library_id", "created_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_candidates_library_id_session_id",
                table: "scan_candidates",
                columns: new[] { "library_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_candidates_matched_release_id",
                table: "scan_candidates",
                column: "matched_release_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_candidates_session_id_seq",
                table: "scan_candidates",
                columns: new[] { "session_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scan_candidates_session_id_snap_id",
                table: "scan_candidates",
                columns: new[] { "session_id", "snap_id" });

            migrationBuilder.CreateIndex(
                name: "ux_scan_candidates_idempotency",
                table: "scan_candidates",
                columns: new[] { "session_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key is not null");

            migrationBuilder.CreateIndex(
                name: "ix_scan_sessions_created_at",
                table: "scan_sessions",
                column: "created_at",
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_scan_sessions_library_id_default_location_id",
                table: "scan_sessions",
                columns: new[] { "library_id", "default_location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_sessions_library_id_status_created_at",
                table: "scan_sessions",
                columns: new[] { "library_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_sessions_user_id",
                table: "scan_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_snaps_asset_id",
                table: "scan_snaps",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_snaps_library_id_session_id",
                table: "scan_snaps",
                columns: new[] { "library_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_snaps_session_id",
                table: "scan_snaps",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_setup_codes_code_hash",
                table: "setup_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tags_name_per_library",
                table: "tags",
                columns: new[] { "library_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_claims_user_id",
                table: "user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_user_id",
                table: "user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_passkeys_user_id",
                table: "user_passkeys",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                table: "users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_user_name",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_works_cover_asset_id",
                table: "works",
                column: "cover_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_works_kind_sort_title",
                table: "works",
                columns: new[] { "kind", "sort_title" });

            migrationBuilder.CreateIndex(
                name: "ix_works_normalized_title_trgm",
                table: "works",
                column: "normalized_title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "device_pairings");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "item_feature_prints");

            migrationBuilder.DropTable(
                name: "item_tags");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "library_invites");

            migrationBuilder.DropTable(
                name: "library_members");

            migrationBuilder.DropTable(
                name: "match_feedback");

            migrationBuilder.DropTable(
                name: "openiddict_scopes");

            migrationBuilder.DropTable(
                name: "openiddict_tokens");

            migrationBuilder.DropTable(
                name: "provider_cache");

            migrationBuilder.DropTable(
                name: "provider_quota");

            migrationBuilder.DropTable(
                name: "release_identifiers");

            migrationBuilder.DropTable(
                name: "role_claims");

            migrationBuilder.DropTable(
                name: "setup_codes");

            migrationBuilder.DropTable(
                name: "user_claims");

            migrationBuilder.DropTable(
                name: "user_logins");

            migrationBuilder.DropTable(
                name: "user_passkeys");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "scan_candidates");

            migrationBuilder.DropTable(
                name: "openiddict_authorizations");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "scan_snaps");

            migrationBuilder.DropTable(
                name: "openiddict_applications");

            migrationBuilder.DropTable(
                name: "releases");

            migrationBuilder.DropTable(
                name: "scan_sessions");

            migrationBuilder.DropTable(
                name: "platforms");

            migrationBuilder.DropTable(
                name: "works");

            migrationBuilder.DropTable(
                name: "locations");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "libraries");
        }
    }
}
