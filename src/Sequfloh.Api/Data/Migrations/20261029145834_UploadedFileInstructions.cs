using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class UploadedFileInstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUploadedFile",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // existing uploaded instructions: imported files keep the file name (without its extension) as the title, and Procedure Instructions are their file.
            // Only the flag is set; any built-in content they hold stays stored and is simply no longer shown or copied onto new work orders.
            migrationBuilder.Sql(@"UPDATE sqf.sequfloh_work_instructions w SET ""IsUploadedFile"" = true
WHERE EXISTS (SELECT 1 FROM sqf.attachments a WHERE a.""OwnerType"" = 'SequflohWorkInstruction' AND a.""OwnerId"" = w.""Id"" AND NOT a.""IsArchived""
  AND (w.""Type"" = 6 OR lower(btrim(regexp_replace(a.""FileName"", '\.[^.]*$', ''))) = lower(btrim(w.""Title""))));");

            // the built-in Administrator role holds every permission, including Remove WI from order and any added since it was created
            migrationBuilder.Sql($"""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", p FROM sqf.roles, unnest(ARRAY[{string.Join(", ", Sequfloh.Domain.Security.Permissions.All.Except(Sequfloh.Domain.Security.Permissions.AssignOnly).Select(x => $"'{x}'"))}]) AS p WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUploadedFile",
                schema: "sqf",
                table: "sequfloh_work_instructions");
        }
    }
}
