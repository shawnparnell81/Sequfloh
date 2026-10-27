using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartUpc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Upc",
                schema: "sqf",
                table: "parts",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpcKey",
                schema: "sqf",
                table: "parts",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_parts_UpcKey",
                schema: "sqf",
                table: "parts",
                column: "UpcKey",
                unique: true,
                filter: "\"UpcKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_parts_UpcKey",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "Upc",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "UpcKey",
                schema: "sqf",
                table: "parts");
        }
    }
}
