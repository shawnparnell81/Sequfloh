using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class TestPartPassFail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ManualAt",
                schema: "sqf",
                table: "sequfloh_test_part_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualBy",
                schema: "sqf",
                table: "sequfloh_test_part_results",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualReason",
                schema: "sqf",
                table: "sequfloh_test_part_results",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualResult",
                schema: "sqf",
                table: "sequfloh_test_part_results",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualAt",
                schema: "sqf",
                table: "sequfloh_test_part_results");

            migrationBuilder.DropColumn(
                name: "ManualBy",
                schema: "sqf",
                table: "sequfloh_test_part_results");

            migrationBuilder.DropColumn(
                name: "ManualReason",
                schema: "sqf",
                table: "sequfloh_test_part_results");

            migrationBuilder.DropColumn(
                name: "ManualResult",
                schema: "sqf",
                table: "sequfloh_test_part_results");
        }
    }
}
