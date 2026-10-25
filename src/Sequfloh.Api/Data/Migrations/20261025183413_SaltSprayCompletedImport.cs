using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class SaltSprayCompletedImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSaltSprayTest",
                schema: "sqf",
                table: "imported_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSaltSprayTest",
                schema: "sqf",
                table: "imported_files");
        }
    }
}
