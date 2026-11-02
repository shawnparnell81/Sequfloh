using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamilyShownFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShownFields",
                schema: "sqf",
                table: "sequfloh_reference_values",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShownFields",
                schema: "sqf",
                table: "sequfloh_reference_values");
        }
    }
}
