using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class WeightWorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FamilyName",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FamilyId",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "FamilyName",
                schema: "sqf",
                table: "sequfloh_tracking_records");
        }
    }
}
