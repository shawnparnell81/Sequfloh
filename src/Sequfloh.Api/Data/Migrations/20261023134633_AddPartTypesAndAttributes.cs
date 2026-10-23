using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartTypesAndAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributeText",
                schema: "sqf",
                table: "parts",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Attributes",
                schema: "sqf",
                table: "parts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TypeId",
                schema: "sqf",
                table: "parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_parts_TypeId",
                schema: "sqf",
                table: "parts",
                column: "TypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_parts_TypeId",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "AttributeText",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "Attributes",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "TypeId",
                schema: "sqf",
                table: "parts");
        }
    }
}
