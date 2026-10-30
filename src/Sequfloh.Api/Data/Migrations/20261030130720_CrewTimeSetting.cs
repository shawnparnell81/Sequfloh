using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class CrewTimeSetting : Migration
    {
        public const string DefaultSql = """
            UPDATE sqf.sequfloh_tracking_records r SET "UseCrewTime" = true
            WHERE EXISTS (SELECT 1 FROM sqf.sequfloh_crew_sessions c WHERE c."TrackingRecordId" = r."Id" AND c."WorkerId" IS NULL
                            AND COALESCE(c."Note", '') <> 'Creating the work order')
              AND NOT EXISTS (SELECT 1 FROM sqf.sequfloh_crew_sessions d WHERE d."TrackingRecordId" = r."Id" AND d."WorkerId" IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM sqf.sequfloh_time_entries t WHERE t."TrackingRecordId" = r."Id" AND t."VoidedAt" IS NULL);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseCrewTime",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Existing work orders start with crew time off, so the crew entries made automatically when a work order was created stop counting.
            // Only a work order whose crew time was clearly typed or clocked by hand (any nameless crew entry other than the automatic
            // "Creating the work order" one) and that has no named or day-worker time keeps crew time on.
            migrationBuilder.Sql(DefaultSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseCrewTime",
                schema: "sqf",
                table: "sequfloh_tracking_records");
        }
    }
}
