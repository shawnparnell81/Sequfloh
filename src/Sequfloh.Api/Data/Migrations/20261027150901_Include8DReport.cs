using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class Include8DReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Include8D",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Include8D",
                schema: "sqf",
                table: "sequfloh_projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Include8D",
                schema: "sqf",
                table: "sequfloh_inspection_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // records that already carry 8D files keep showing the block
            foreach (var (table, type) in new[] { ("sequfloh_tracking_records", "SequflohTrackingRecord"), ("sequfloh_projects", "SequflohProject"), ("sequfloh_inspection_requests", "SequflohInspectionRequest") })
                migrationBuilder.Sql($"UPDATE sqf.{table} t SET \"Include8D\" = true WHERE EXISTS (SELECT 1 FROM sqf.attachments a WHERE a.\"OwnerType\" = '8D:{type}' AND a.\"OwnerId\" = t.\"Id\" AND NOT a.\"IsArchived\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Include8D",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "Include8D",
                schema: "sqf",
                table: "sequfloh_projects");

            migrationBuilder.DropColumn(
                name: "Include8D",
                schema: "sqf",
                table: "sequfloh_inspection_requests");
        }
    }
}
