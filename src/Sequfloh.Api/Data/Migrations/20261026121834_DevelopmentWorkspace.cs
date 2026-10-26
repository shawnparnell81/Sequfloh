using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class DevelopmentWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DevelopmentLine",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DevelopmentLine",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DevelopmentLine",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "DevelopmentLine",
                schema: "sqf",
                table: "sequfloh_tracking_records");
        }
    }
}
