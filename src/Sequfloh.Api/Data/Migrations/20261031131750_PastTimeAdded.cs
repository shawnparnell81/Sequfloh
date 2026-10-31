using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PastTimeAdded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddedBy",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddedReason",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            // the built-in Administrator role holds every permission, including Add past time
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", 'sequfloh.add_past_time' FROM sqf.roles WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM sqf.role_permissions WHERE "Permission" = 'sequfloh.add_past_time';""");

            migrationBuilder.DropColumn(
                name: "AddedBy",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "AddedReason",
                schema: "sqf",
                table: "sequfloh_time_entries");
        }
    }
}
