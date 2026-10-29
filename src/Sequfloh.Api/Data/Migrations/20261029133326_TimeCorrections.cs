using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimeCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedAt",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                schema: "sqf",
                table: "sequfloh_crew_sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedAt",
                schema: "sqf",
                table: "sequfloh_crew_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                schema: "sqf",
                table: "sequfloh_crew_sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TimeChangedAt",
                schema: "sqf",
                table: "sequfloh_chargeback_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeChangedNote",
                schema: "sqf",
                table: "sequfloh_chargeback_reports",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VoidReason",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "VoidedAt",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "VoidReason",
                schema: "sqf",
                table: "sequfloh_crew_sessions");

            migrationBuilder.DropColumn(
                name: "VoidedAt",
                schema: "sqf",
                table: "sequfloh_crew_sessions");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                schema: "sqf",
                table: "sequfloh_crew_sessions");

            migrationBuilder.DropColumn(
                name: "TimeChangedAt",
                schema: "sqf",
                table: "sequfloh_chargeback_reports");

            migrationBuilder.DropColumn(
                name: "TimeChangedNote",
                schema: "sqf",
                table: "sequfloh_chargeback_reports");
        }
    }
}
