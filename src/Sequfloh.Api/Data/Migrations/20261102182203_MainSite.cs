using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class MainSite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMainSite",
                schema: "sqf",
                table: "sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // MAIN is the organization's main site
            migrationBuilder.Sql("UPDATE sqf.sites SET \"IsMainSite\" = true WHERE \"Code\" = 'MAIN';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsMainSite",
                schema: "sqf",
                table: "sites");
        }
    }
}
