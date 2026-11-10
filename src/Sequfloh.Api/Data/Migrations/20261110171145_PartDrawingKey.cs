using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartDrawingKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DrawingKey",
                schema: "sqf",
                table: "parts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                computedColumnSql: "NULLIF(upper(regexp_replace(btrim(\"DrawingNumber\"), '[[:space:]_/\\\\-]+', '', 'g')), '')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_parts_DrawingKey",
                schema: "sqf",
                table: "parts",
                column: "DrawingKey");

            migrationBuilder.CreateIndex(
                name: "IX_parts_PartNumberKey",
                schema: "sqf",
                table: "parts",
                column: "PartNumberKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_parts_DrawingKey",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "IX_parts_PartNumberKey",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "DrawingKey",
                schema: "sqf",
                table: "parts");
        }
    }
}
