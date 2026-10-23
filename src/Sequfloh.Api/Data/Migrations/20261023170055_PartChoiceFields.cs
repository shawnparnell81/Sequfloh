using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartChoiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAssembly",
                schema: "sqf",
                table: "parts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LotControl",
                schema: "sqf",
                table: "parts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartClass",
                schema: "sqf",
                table: "parts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // existing values carried over; the old assembly type text is kept as it was
            migrationBuilder.Sql(@"UPDATE sqf.parts SET ""IsAssembly"" = TRUE WHERE ""PartType"" IN (1, 3) OR (COALESCE(TRIM(""AssemblyType""), '') NOT IN ('', 'No', 'no', 'NO', 'N', 'n'));");
            migrationBuilder.Sql(@"UPDATE sqf.parts SET ""LotControl"" = CASE WHEN ""IsLotControlled"" THEN 'Yes' ELSE 'No' END;");
            migrationBuilder.Sql(@"UPDATE sqf.parts SET ""UnitOfMeasure"" = 'Each' WHERE UPPER(TRIM(""UnitOfMeasure"")) IN ('EA', 'EACH');");

            // product families: Strut and Air Strut become Struts and Air Struts (unless already there), then the list follows the agreed order;
            // Coil Spring is added by the startup seed
            foreach (var (from, to) in new[] { ("Strut", "Struts"), ("Air Strut", "Air Struts") })
            {
                var code = to.ToUpperInvariant().Replace(' ', '_');
                var oldCode = from.ToUpperInvariant().Replace(' ', '_');
                migrationBuilder.Sql($@"UPDATE sqf.sequfloh_reference_values SET ""Label"" = '{to}', ""Code"" = '{code}'
                    WHERE ""ListKey"" = 'product-families' AND ""Code"" = '{oldCode}' AND ""Label"" = '{from}'
                    AND NOT EXISTS (SELECT 1 FROM sqf.sequfloh_reference_values x WHERE x.""ListKey"" = 'product-families' AND x.""Code"" = '{code}');");
                migrationBuilder.Sql($@"UPDATE sqf.parts SET ""ProductFamily"" = '{to}' WHERE ""ProductFamily"" = '{from}'
                    AND ""TypeId"" IN (SELECT ""Id"" FROM sqf.sequfloh_reference_values WHERE ""ListKey"" = 'product-families' AND ""Label"" = '{to}');");
            }
            var order = new[] { "COMPLETE_STRUT_ASSEMBLY__CSA", "FUEL_PUMP", "FUEL_INJECTOR", "BRAKE_WEAR_SENSOR", "GAS_LIFT", "ELECTRIC_LIFT", "COIL_SPRING", "STRUTS", "AIR_STRUTS" };
            for (var i = 0; i < order.Length; i++)
                migrationBuilder.Sql($@"UPDATE sqf.sequfloh_reference_values SET ""SortOrder"" = {(i + 1) * 10} WHERE ""ListKey"" = 'product-families' AND ""Code"" = '{order[i]}';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAssembly",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "LotControl",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "PartClass",
                schema: "sqf",
                table: "parts");
        }
    }
}
