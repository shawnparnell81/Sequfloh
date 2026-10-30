using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreationCrewToCreator : Migration
    {
        /// <summary>
        /// System correction for the legacy "Creating the work order" crew entries (one nameless worker, made automatically before creation time
        /// was saved as the creator's own time). On work orders that do not use crew time, each finished one becomes named time of the work
        /// order's creator, unless the creator already has named time overlapping it (then it is not added again, so nothing is counted twice).
        /// The crew entry is voided with a System reason (kept in the history, not deleted) and every work order touched gets an audit entry.
        /// Work orders that use crew time are left as they are.
        /// </summary>
        public const string Sql = """
            CREATE TEMP TABLE legacy_creation ON COMMIT DROP AS
            SELECT c."Id" AS "CrewId", c."TrackingRecordId", c."TrackingNumber", c."StartedAt", c."StoppedAt", c."PausedSeconds",
                   r."CreatedById" AS "UserId", u."DisplayName" AS "UserName", r."SiteId",
                   GREATEST(0, floor(extract(epoch FROM c."StoppedAt" - c."StartedAt"))::bigint - c."PausedSeconds") AS "Seconds",
                   EXISTS (SELECT 1 FROM sqf.sequfloh_time_entries t WHERE t."TrackingRecordId" = c."TrackingRecordId" AND t."UserId" = r."CreatedById" AND t."VoidedAt" IS NULL
                             AND t."StartedAt" < c."StoppedAt" AND COALESCE(t."StoppedAt", 'infinity') > c."StartedAt") AS "Overlaps"
            FROM sqf.sequfloh_crew_sessions c
            JOIN sqf.sequfloh_tracking_records r ON r."Id" = c."TrackingRecordId"
            JOIN sqf.users u ON u."Id" = r."CreatedById"
            WHERE c."WorkerId" IS NULL AND c."CrewSize" = 1 AND c."Note" = 'Creating the work order' AND c."VoidedAt" IS NULL AND c."StoppedAt" IS NOT NULL
              AND NOT r."UseCrewTime";

            INSERT INTO sqf.sequfloh_time_entries ("Id", "TrackingRecordId", "TrackingNumber", "UserId", "UserName", "StartedAt", "StoppedAt", "Seconds", "PausedSeconds", "Events", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), "TrackingRecordId", "TrackingNumber", "UserId", left("UserName", 200), "StartedAt", "StoppedAt", "Seconds", "PausedSeconds",
                   jsonb_build_array(jsonb_build_object('kind', 'Start', 'at', "StartedAt"), jsonb_build_object('kind', 'Stop', 'at', "StoppedAt")), now(), now()
            FROM legacy_creation WHERE NOT "Overlaps";

            UPDATE sqf.sequfloh_crew_sessions c SET "VoidedAt" = now(), "VoidedBy" = 'System',
                   "VoidReason" = left('System correction: creation time attributed to ' || l."UserName" || CASE WHEN l."Overlaps" THEN ' (already clocked by name, not added again)' ELSE '' END, 500)
            FROM legacy_creation l WHERE c."Id" = l."CrewId";

            INSERT INTO sqf.audit_entries ("OccurredAt", "UserId", "UserName", "Action", "EntityType", "EntityId", "Description", "SiteId")
            SELECT now(), NULL, 'System', 'SystemCorrection', 'SequflohTrackingRecord', "TrackingRecordId",
                   left(max("TrackingNumber") || ': system correction, automatic Creating the work order crew time now counted as named time of ' || max("UserName")
                        || CASE WHEN bool_or("Overlaps") THEN ' (overlapping time already clocked by name was not added again)' ELSE '' END, 4000), (array_agg("SiteId"))[1]
            FROM legacy_creation GROUP BY "TrackingRecordId";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
