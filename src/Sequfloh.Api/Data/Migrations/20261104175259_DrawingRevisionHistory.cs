using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class DrawingRevisionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangeReference",
                schema: "sqf",
                table: "drawing_revisions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                schema: "sqf",
                table: "drawing_revisions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleasedAt",
                schema: "sqf",
                table: "drawing_revisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleasedByName",
                schema: "sqf",
                table: "drawing_revisions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "sqf",
                table: "drawing_revisions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Existing files stay where they are. This only records which file is current.
            // A blank revision letter becomes '-'. A drawing whose current letter does not match a file takes the letter of its newest file.
            migrationBuilder.Sql("""
                UPDATE sqf.drawing_revisions v
                SET "Revision" = '-'
                WHERE btrim(v."Revision") = ''
                  AND NOT EXISTS (
                    SELECT 1 FROM sqf.drawing_revisions o
                    WHERE o."DrawingId" = v."DrawingId" AND o."Id" <> v."Id" AND o."Revision" = '-'
                  );

                UPDATE sqf.drawings d
                SET "CurrentRevision" = sub."Revision"
                FROM (
                  SELECT DISTINCT ON ("DrawingId") "DrawingId", "Revision"
                  FROM sqf.drawing_revisions
                  ORDER BY "DrawingId", "CreatedAt" DESC, "Id" DESC
                ) sub
                WHERE d."Id" = sub."DrawingId"
                  AND NOT EXISTS (
                    SELECT 1 FROM sqf.drawing_revisions v
                    WHERE v."DrawingId" = d."Id" AND upper(btrim(v."Revision")) = upper(btrim(d."CurrentRevision"))
                  );

                UPDATE sqf.drawings d
                SET "CurrentRevision" = picked."Revision"
                FROM (
                  SELECT DISTINCT ON (v."DrawingId") v."DrawingId", v."Revision"
                  FROM sqf.drawing_revisions v
                  JOIN sqf.drawings d2 ON d2."Id" = v."DrawingId"
                  WHERE upper(btrim(v."Revision")) = upper(btrim(d2."CurrentRevision"))
                    AND v."Revision" <> d2."CurrentRevision"
                  ORDER BY v."DrawingId", v."CreatedAt" DESC, v."Id" DESC
                ) picked
                WHERE d."Id" = picked."DrawingId";

                UPDATE sqf.drawings
                SET "CurrentRevision" = '-'
                WHERE btrim(COALESCE("CurrentRevision", '')) = '';

                UPDATE sqf.drawing_revisions v
                SET "Status" = CASE WHEN v."Revision" = d."CurrentRevision" THEN 1 ELSE 2 END
                FROM sqf.drawings d
                WHERE d."Id" = v."DrawingId";

                UPDATE sqf.drawing_revisions
                SET "Reason" = "Notes"
                WHERE "Reason" IS NULL AND "Notes" IS NOT NULL AND btrim("Notes") <> '';

                UPDATE sqf.drawing_revisions
                SET "ReleasedAt" = "CreatedAt", "ReleasedByName" = NULLIF("CreatedByName", '')
                WHERE "Status" = 1 AND "ReleasedAt" IS NULL;

                ALTER TABLE sqf.drawing_revisions ALTER COLUMN "Status" DROP DEFAULT;
                """);

            // The built-in Administrator role holds every permission, including Upload a new drawing revision and Release a pending drawing revision.
            migrationBuilder.Sql($"""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", p FROM sqf.roles, unnest(ARRAY[{string.Join(", ", Sequfloh.Domain.Security.Permissions.All.Except(Sequfloh.Domain.Security.Permissions.AssignOnly).Select(x => $"'{x}'"))}]) AS p WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_drawing_revisions_DrawingId_Status",
                schema: "sqf",
                table: "drawing_revisions",
                columns: new[] { "DrawingId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_drawing_revisions_DrawingId_Status",
                schema: "sqf",
                table: "drawing_revisions");

            migrationBuilder.DropColumn(
                name: "ChangeReference",
                schema: "sqf",
                table: "drawing_revisions");

            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "sqf",
                table: "drawing_revisions");

            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                schema: "sqf",
                table: "drawing_revisions");

            migrationBuilder.DropColumn(
                name: "ReleasedByName",
                schema: "sqf",
                table: "drawing_revisions");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "sqf",
                table: "drawing_revisions");
        }
    }
}
