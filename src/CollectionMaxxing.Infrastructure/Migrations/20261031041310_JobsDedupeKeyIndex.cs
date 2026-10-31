using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CollectionMaxxing.Infrastructure.Migrations
{
    /// <summary>
    /// Every job insert with a dedupe key looks the key up in <c>payload ->> 'key'</c>, and the recurring scheduler does it every
    /// few seconds across all finished rows. An expression index makes that lookup an index probe. EF's model cannot describe an
    /// expression index, so it lives only here (and in the database); the model snapshot is unchanged.
    /// </summary>
    public partial class JobsDedupeKeyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Partial: most jobs have no dedupe key, and an equality on the key can only match rows that have one.
            migrationBuilder.Sql(
                "CREATE INDEX ix_jobs_payload_key ON jobs ((payload ->> 'key')) WHERE (payload ->> 'key') IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_jobs_payload_key;");
        }
    }
}
