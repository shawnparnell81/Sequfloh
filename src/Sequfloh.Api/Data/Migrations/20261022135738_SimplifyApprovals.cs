using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep the history of earlier reviews, approvals and sign-offs in the audit trail before the columns go.
            migrationBuilder.Sql("""
                INSERT INTO sqf.audit_entries ("OccurredAt", "UserId", "UserName", "Action", "EntityType", "EntityId", "Description", "SiteId")
                SELECT now(), NULL, 'System', 'Updated', 'SequflohWorkInstruction', w."Id",
                       left('Approval history kept when review and approval steps were removed from ' || w."Number" || ' rev ' || w."Revision" || ':'
                         || coalesce(' reviewed by ' || coalesce(rv."DisplayName", 'unknown') || ' on ' || to_char(w."ReviewedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC;', '')
                         || coalesce(' approved by ' || coalesce(ap."DisplayName", 'unknown') || ' on ' || to_char(w."ApprovedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC;', '')
                         || coalesce(' release recommended by ' || coalesce(rr."DisplayName", 'unknown') || ' on ' || to_char(w."ReleaseRecommendedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC;', '')
                         || CASE w."Status" WHEN 1 THEN ' status In Development is now Draft.' WHEN 2 THEN ' status Pending Review is now Draft.' WHEN 3 THEN ' status Changes Requested is now Draft.'
                                            WHEN 4 THEN ' status Pending Approval is now Draft.' WHEN 5 THEN ' status Approved is now Draft.' WHEN 9 THEN ' status Archived is now Obsolete.' ELSE '' END, 4000),
                       w."SiteId"
                FROM sqf.sequfloh_work_instructions w
                LEFT JOIN sqf.users rv ON rv."Id" = w."ReviewedById"
                LEFT JOIN sqf.users ap ON ap."Id" = w."ApprovedById"
                LEFT JOIN sqf.users rr ON rr."Id" = w."ReleaseRecommendedById"
                WHERE w."ReviewedAt" IS NOT NULL OR w."ApprovedAt" IS NOT NULL OR w."ReleaseRecommendedAt" IS NOT NULL OR w."Status" IN (1, 2, 3, 4, 5, 9);
                """);
            migrationBuilder.Sql("""
                INSERT INTO sqf.audit_entries ("OccurredAt", "UserId", "UserName", "Action", "EntityType", "EntityId", "Description", "SiteId")
                SELECT now(), NULL, 'System', 'Updated', 'SequflohTrackingRecord', t."Id",
                       left('Sign-off and review history kept when sign-offs and reviews were removed from ' || t."Number" || ':'
                         || coalesce((SELECT string_agg(' ' || coalesce(s->>'role', 'sign-off') || ' signed by ' || coalesce(s->>'userName', 'unknown') || ' at ' || (s->>'signedAt') || coalesce(' (' || (s->>'meaning') || ')', '') || ';', '')
                                      FROM jsonb_array_elements(CASE WHEN jsonb_typeof(t."Signoffs") = 'array' THEN t."Signoffs" ELSE '[]'::jsonb END) s WHERE coalesce(s->>'signedAt', '') <> ''), '')
                         || coalesce(' Quality review by ' || coalesce(q."DisplayName", 'unknown') || ' on ' || to_char(t."QualityReviewedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC;', '')
                         || coalesce(' Engineering review by ' || coalesce(e."DisplayName", 'unknown') || ' on ' || to_char(t."EngineeringReviewedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC;', '')
                         || CASE WHEN t."ApprovalStatus" NOT IN ('NotRequired', '') THEN ' approval status was ' || t."ApprovalStatus" || ';' ELSE '' END
                         || CASE t."Status" WHEN 5 THEN ' status Pending Inspection is now Completed.' WHEN 6 THEN ' status Pending Review is now Completed.' WHEN 9 THEN ' status Rejected is now Failed.' ELSE '' END
                         || coalesce(' Rejection reason: ' || t."RejectionReason", ''), 4000),
                       t."SiteId"
                FROM sqf.sequfloh_tracking_records t
                LEFT JOIN sqf.users q ON q."Id" = t."QualityReviewedById"
                LEFT JOIN sqf.users e ON e."Id" = t."EngineeringReviewedById"
                WHERE t."QualityReviewedAt" IS NOT NULL OR t."EngineeringReviewedAt" IS NOT NULL OR t."ApprovalStatus" NOT IN ('NotRequired', '')
                   OR t."Status" IN (5, 6, 9) OR t."RejectionReason" IS NOT NULL
                   OR (jsonb_typeof(t."Signoffs") = 'array' AND jsonb_path_exists(t."Signoffs", '$[*].signedAt'));
                """);

            // Work instructions: every in-between review state becomes Draft; Archived becomes Obsolete.
            migrationBuilder.Sql("""
                UPDATE sqf.sequfloh_work_instructions SET "Status" = 0 WHERE "Status" IN (1, 2, 3, 4, 5);
                UPDATE sqf.sequfloh_work_instructions SET "Status" = 8, "ObsoleteAt" = coalesce("ObsoleteAt", "UpdatedAt") WHERE "Status" = 9;
                UPDATE sqf.sequfloh_work_instruction_transitions SET "FromStatus" = 0 WHERE "FromStatus" IN (1, 2, 3, 4, 5);
                UPDATE sqf.sequfloh_work_instruction_transitions SET "ToStatus" = 0 WHERE "ToStatus" IN (1, 2, 3, 4, 5);
                UPDATE sqf.sequfloh_work_instruction_transitions SET "FromStatus" = 8 WHERE "FromStatus" = 9;
                UPDATE sqf.sequfloh_work_instruction_transitions SET "ToStatus" = 8 WHERE "ToStatus" = 9;
                """);

            // Tracking: work waiting for inspection or review is finished work; rejected work is failed work.
            migrationBuilder.Sql("""
                UPDATE sqf.sequfloh_tracking_records
                   SET "Status" = 7, "ActualCompletion" = coalesce("ActualCompletion", "LastTransitionAt", "UpdatedAt"), "ActiveOperatorId" = NULL,
                       "CurrentStateStartedAt" = coalesce("ActualCompletion", "LastTransitionAt", "UpdatedAt")
                 WHERE "Status" IN (5, 6);
                UPDATE sqf.sequfloh_tracking_records SET "Status" = 8, "FailureReason" = coalesce("FailureReason", "RejectionReason") WHERE "Status" = 9;
                UPDATE sqf.sequfloh_task_events SET "FromStatus" = 7 WHERE "FromStatus" IN (5, 6);
                UPDATE sqf.sequfloh_task_events SET "ToStatus" = 7 WHERE "ToStatus" IN (5, 6);
                UPDATE sqf.sequfloh_task_events SET "FromStatus" = 8 WHERE "FromStatus" = 9;
                UPDATE sqf.sequfloh_task_events SET "ToStatus" = 8 WHERE "ToStatus" = 9;
                """);

            // Permissions: anyone who could review or approve instructions can now release them; removed permissions are dropped from roles.
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT DISTINCT "RoleId", 'sequfloh.release_work_instruction' FROM sqf.role_permissions
                 WHERE "Permission" IN ('sequfloh.approve_work_instruction', 'sequfloh.review_work_instruction')
                ON CONFLICT DO NOTHING;
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT DISTINCT "RoleId", 'sequfloh.obsolete_work_instruction' FROM sqf.role_permissions
                 WHERE "Permission" = 'sequfloh.archive_work_instruction'
                ON CONFLICT DO NOTHING;
                DELETE FROM sqf.role_permissions WHERE "Permission" IN ('sequfloh.reject_task', 'sequfloh.submit_work_instruction', 'sequfloh.review_work_instruction',
                    'sequfloh.approve_work_instruction', 'sequfloh.archive_work_instruction', 'sequfloh.restore_work_instruction');
                """);

            // Reference lists that described the removed steps.
            migrationBuilder.Sql("""
                DELETE FROM sqf.sequfloh_reference_values WHERE "ListKey" = 'lifecycle-statuses' AND "Code" IN ('IN_DEVELOPMENT', 'PENDING_REVIEW', 'CHANGES_REQUESTED', 'PENDING_APPROVAL', 'APPROVED', 'ARCHIVED');
                DELETE FROM sqf.sequfloh_reference_values WHERE "ListKey" = 'activity-statuses' AND "Code" IN ('PENDING_INSPECTION', 'PENDING_REVIEW', 'REJECTED');
                DELETE FROM sqf.sequfloh_reference_values WHERE "ListKey" = 'rejection-reasons';
                DELETE FROM sqf.sequfloh_reference_values WHERE "ListKey" = 'evidence-types' AND "Code" = 'SIGNATURE' AND "IsSeeded";
                DELETE FROM sqf.sequfloh_reference_values WHERE "ListKey" = 'approval-action-types' AND "Code" NOT IN ('RELEASE', 'CREATE_REVISION', 'MARK_OBSOLETE');
                UPDATE sqf.sequfloh_reference_values SET "ListKey" = 'lifecycle-actions'
                 WHERE "ListKey" = 'approval-action-types'
                   AND NOT EXISTS (SELECT 1 FROM sqf.sequfloh_reference_values x WHERE x."ListKey" = 'lifecycle-actions' AND x."Code" = sqf.sequfloh_reference_values."Code");
                DELETE FROM sqf.sequfloh_reference_values WHERE "ListKey" = 'approval-action-types';
                """);


            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_parts_lists_users_ApprovedById",
                schema: "sqf",
                table: "sequfloh_parts_lists");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_tracking_records_users_EngineeringReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_tracking_records_users_QualityReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_work_instructions_users_ApprovedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_work_instructions_users_ApproverId",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_work_instructions_users_ReleaseRecommendedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_work_instructions_users_ReviewedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_work_instructions_users_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_work_instructions_ApprovedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_work_instructions_ApproverId",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_work_instructions_ReleaseRecommendedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_work_instructions_ReviewedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_work_instructions_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_work_instructions_Status_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_tracking_records_ApprovalStatus_OwnerId",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_tracking_records_EngineeringReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_tracking_records_QualityReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ApprovedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ApproverId",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ReleaseRecommendedAt",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ReleaseRecommendedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ReviewedById",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "StatusBeforeArchive",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "EngineeringReviewRequired",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "EngineeringReviewedAt",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "EngineeringReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QualityReviewRequired",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QualityReviewedAt",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QualityReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "Signoffs",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.RenameColumn(
                name: "ApprovedById",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                newName: "ReleasedById");

            migrationBuilder.RenameColumn(
                name: "ApprovedAt",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                newName: "ReleasedAt");

            migrationBuilder.RenameIndex(
                name: "IX_sequfloh_parts_lists_ApprovedById",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                newName: "IX_sequfloh_parts_lists_ReleasedById");

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_parts_lists_users_ReleasedById",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                column: "ReleasedById",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_parts_lists_users_ReleasedById",
                schema: "sqf",
                table: "sequfloh_parts_lists");

            migrationBuilder.RenameColumn(
                name: "ReleasedById",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                newName: "ApprovedById");

            migrationBuilder.RenameColumn(
                name: "ReleasedAt",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                newName: "ApprovedAt");

            migrationBuilder.RenameIndex(
                name: "IX_sequfloh_parts_lists_ReleasedById",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                newName: "IX_sequfloh_parts_lists_ApprovedById");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApproverId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleaseRecommendedAt",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReleaseRecommendedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusBeforeArchive",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalStatus",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EngineeringReviewRequired",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EngineeringReviewedAt",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EngineeringReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "QualityReviewRequired",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QualityReviewedAt",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QualityReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Signoffs",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ApprovedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ApproverId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ApproverId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ReleaseRecommendedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReleaseRecommendedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ReviewedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_Status_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                columns: new[] { "Status", "ReviewerId" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_ApprovalStatus_OwnerId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "ApprovalStatus", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_EngineeringReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "EngineeringReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_QualityReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "QualityReviewedById");

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_parts_lists_users_ApprovedById",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                column: "ApprovedById",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_tracking_records_users_EngineeringReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "EngineeringReviewedById",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_tracking_records_users_QualityReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "QualityReviewedById",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_work_instructions_users_ApprovedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ApprovedById",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_work_instructions_users_ApproverId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ApproverId",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_work_instructions_users_ReleaseRecommendedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReleaseRecommendedById",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_work_instructions_users_ReviewedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReviewedById",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_work_instructions_users_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReviewerId",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
