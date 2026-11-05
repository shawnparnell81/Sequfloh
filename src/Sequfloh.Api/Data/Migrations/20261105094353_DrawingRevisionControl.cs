using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class DrawingRevisionControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachedDrawingNumber",
                schema: "sqf",
                table: "tracking_record_drawings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AttachedRevisionId",
                schema: "sqf",
                table: "tracking_record_drawings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DrawingRevision",
                schema: "sqf",
                table: "parts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DrawingNumber",
                schema: "sqf",
                table: "drawing_revisions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Copy the drawing number that was already on each revision's drawing, and the revision file a work order was pointing at.
            // If a drawing somehow has more than one current or pending file, keep the newest and mark the others superseded before the unique indexes are added.
            migrationBuilder.Sql("""
                UPDATE sqf.drawing_revisions v
                SET "DrawingNumber" = d."DrawingNumber"
                FROM sqf.drawings d
                WHERE d."Id" = v."DrawingId" AND v."DrawingNumber" IS NULL AND d."DrawingNumber" IS NOT NULL;

                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "DrawingId" ORDER BY "ReleasedAt" DESC NULLS LAST, "CreatedAt" DESC, "Id" DESC) AS n
                    FROM sqf.drawing_revisions
                    WHERE "Status" = 1
                )
                UPDATE sqf.drawing_revisions v
                SET "Status" = 2
                FROM ranked r
                WHERE v."Id" = r."Id" AND r.n > 1;

                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "DrawingId" ORDER BY "CreatedAt" DESC, "Id" DESC) AS n
                    FROM sqf.drawing_revisions
                    WHERE "Status" = 0
                )
                UPDATE sqf.drawing_revisions v
                SET "Status" = 2
                FROM ranked r
                WHERE v."Id" = r."Id" AND r.n > 1;

                UPDATE sqf.tracking_record_drawings t
                SET "AttachedDrawingNumber" = s.number,
                    "AttachedRevisionId" = s.rev_id
                FROM (
                    SELECT t2."TrackingRecordId", t2."DrawingId",
                           COALESCE(v."DrawingNumber", d."DrawingNumber") AS number,
                           v."Id" AS rev_id
                    FROM sqf.tracking_record_drawings t2
                    JOIN sqf.drawings d ON d."Id" = t2."DrawingId"
                    LEFT JOIN sqf.drawing_revisions v ON v."DrawingId" = d."Id" AND v."Revision" = t2."AttachedRevision"
                ) s
                WHERE t."TrackingRecordId" = s."TrackingRecordId" AND t."DrawingId" = s."DrawingId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_tracking_record_drawings_AttachedRevisionId",
                schema: "sqf",
                table: "tracking_record_drawings",
                column: "AttachedRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_drawing_revisions_one_current",
                schema: "sqf",
                table: "drawing_revisions",
                column: "DrawingId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_drawing_revisions_one_pending",
                schema: "sqf",
                table: "drawing_revisions",
                column: "DrawingId",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_tracking_record_drawings_drawing_revisions_AttachedRevision~",
                schema: "sqf",
                table: "tracking_record_drawings",
                column: "AttachedRevisionId",
                principalSchema: "sqf",
                principalTable: "drawing_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tracking_record_drawings_drawing_revisions_AttachedRevision~",
                schema: "sqf",
                table: "tracking_record_drawings");

            migrationBuilder.DropIndex(
                name: "IX_tracking_record_drawings_AttachedRevisionId",
                schema: "sqf",
                table: "tracking_record_drawings");

            migrationBuilder.DropIndex(
                name: "IX_drawing_revisions_one_current",
                schema: "sqf",
                table: "drawing_revisions");

            migrationBuilder.DropIndex(
                name: "IX_drawing_revisions_one_pending",
                schema: "sqf",
                table: "drawing_revisions");

            migrationBuilder.DropColumn(
                name: "AttachedDrawingNumber",
                schema: "sqf",
                table: "tracking_record_drawings");

            migrationBuilder.DropColumn(
                name: "AttachedRevisionId",
                schema: "sqf",
                table: "tracking_record_drawings");

            migrationBuilder.DropColumn(
                name: "DrawingRevision",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "DrawingNumber",
                schema: "sqf",
                table: "drawing_revisions");
        }
    }
}
