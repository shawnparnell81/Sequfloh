using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderBuilder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FurtherInspection",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InspectionDescription",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InspectionSubType",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QtyBad",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QtyGood",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Quarantine",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "QuarantineReason",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ValidationFormId",
                schema: "sqf",
                table: "sequfloh_reference_values",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FurtherInspection",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "InspectionDescription",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "InspectionSubType",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QtyBad",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QtyGood",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "Quarantine",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QuarantineReason",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "ValidationFormId",
                schema: "sqf",
                table: "sequfloh_reference_values");
        }
    }
}
