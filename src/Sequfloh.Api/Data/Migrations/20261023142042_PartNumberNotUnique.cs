using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartNumberNotUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_parts_PartNumber",
                schema: "sqf",
                table: "parts");

            migrationBuilder.CreateIndex(
                name: "IX_parts_PartNumber",
                schema: "sqf",
                table: "parts",
                column: "PartNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_parts_PartNumber",
                schema: "sqf",
                table: "parts");

            migrationBuilder.CreateIndex(
                name: "IX_parts_PartNumber",
                schema: "sqf",
                table: "parts",
                column: "PartNumber",
                unique: true);
        }
    }
}
