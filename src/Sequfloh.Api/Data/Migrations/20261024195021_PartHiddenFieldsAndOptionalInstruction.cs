using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartHiddenFieldsAndOptionalInstruction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "WorkInstructionId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "HiddenFields",
                schema: "sqf",
                table: "sequfloh_reference_values",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HiddenFields",
                schema: "sqf",
                table: "parts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HiddenFields",
                schema: "sqf",
                table: "sequfloh_reference_values");

            migrationBuilder.DropColumn(
                name: "HiddenFields",
                schema: "sqf",
                table: "parts");

            migrationBuilder.AlterColumn<Guid>(
                name: "WorkInstructionId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
