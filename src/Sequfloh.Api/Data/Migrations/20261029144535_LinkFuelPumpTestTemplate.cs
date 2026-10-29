using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkFuelPumpTestTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // links the fuel pump test template to the product family named exactly "Fuel Pump" (case and outer spaces ignored), when it has no template yet.
            // The family's name, code and everything else stay as they are. No exact match: nothing happens; an administrator can pick it in Product families.
            migrationBuilder.Sql(Sequfloh.Infrastructure.Modules.Execution.SequflohTestTemplates.LinkFuelPumpSql);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
