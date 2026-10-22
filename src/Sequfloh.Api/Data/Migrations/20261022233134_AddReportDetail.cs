using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CostLines",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "Findings",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "ReportOptions",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "Events",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PausedAt",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PausedSeconds",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "sqf",
                table: "sequfloh_test_files",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "test");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostLines",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "Findings",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "ReportOptions",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "Events",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "PausedAt",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "PausedSeconds",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "sqf",
                table: "sequfloh_test_files");
        }
    }
}
