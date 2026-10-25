using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderNumberCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NumberCode",
                schema: "sqf",
                table: "sequfloh_reference_values",
                type: "text",
                nullable: true);
            migrationBuilder.Sql(@"UPDATE sqf.sequfloh_reference_values SET ""NumberCode"" = 'CSA' WHERE ""ListKey"" = 'product-families' AND ""Label"" = 'Complete Strut Assembly (CSA)' AND ""NumberCode"" IS NULL;");
            migrationBuilder.Sql(@"UPDATE sqf.sequfloh_reference_values SET ""NumberCode"" = 'FP' WHERE ""ListKey"" = 'product-families' AND ""Label"" = 'Fuel Pump' AND ""NumberCode"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NumberCode",
                schema: "sqf",
                table: "sequfloh_reference_values");
        }
    }
}
