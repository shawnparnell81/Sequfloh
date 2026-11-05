using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdjustTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdjustReason",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AdjustedAt",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdjustedBy",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OriginalPausedAt",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OriginalStartedAt",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OriginalStoppedAt",
                schema: "sqf",
                table: "sequfloh_time_entries",
                type: "timestamp with time zone",
                nullable: true);

            // the built-in Administrator role holds every permission, including Correct a clock-off
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", 'sequfloh.adjust_time' FROM sqf.roles WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM sqf.role_permissions WHERE "Permission" = 'sequfloh.adjust_time';""");

            migrationBuilder.DropColumn(
                name: "AdjustReason",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "AdjustedAt",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "AdjustedBy",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "OriginalPausedAt",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "OriginalStartedAt",
                schema: "sqf",
                table: "sequfloh_time_entries");

            migrationBuilder.DropColumn(
                name: "OriginalStoppedAt",
                schema: "sqf",
                table: "sequfloh_time_entries");
        }
    }
}
