using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class DepartmentForceDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DepartmentName",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DepartmentName",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // the built-in Administrator role holds every permission, including deleting a department that is still in use
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", 'admin.delete_departments_in_use' FROM sqf.roles WHERE "IsSystem" OR "Name" = 'Administrator'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM sqf.role_permissions WHERE "Permission" = 'admin.delete_departments_in_use';""");

            migrationBuilder.DropColumn(
                name: "DepartmentName",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "DepartmentName",
                schema: "sqf",
                table: "sequfloh_tracking_records");
        }
    }
}
