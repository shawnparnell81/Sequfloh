using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StatusNote",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusTag",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartsReplaced",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<int>(
                name: "QuantityCompleted",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuantityNotRepaired",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuantityTarget",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusNote",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusTag",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TestColumns",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "StatusNote",
                schema: "sqf",
                table: "parts",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusTag",
                schema: "sqf",
                table: "parts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sequfloh_test_files",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StorageKey = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Batch = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TestDate = table.Column<DateOnly>(type: "date", nullable: true),
                    UploadedByName = table.Column<string>(type: "text", nullable: false),
                    Tested = table.Column<int>(type: "integer", nullable: true),
                    Passed = table.Column<int>(type: "integer", nullable: true),
                    Failed = table.Column<int>(type: "integer", nullable: true),
                    KeyValues = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_test_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_test_files_sequfloh_tracking_records_TrackingRecor~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_test_files_TrackingRecordId",
                schema: "sqf",
                table: "sequfloh_test_files",
                column: "TrackingRecordId");

            // Holds are handled in the ERP: open work orders on hold become paused, keeping the reason.
            migrationBuilder.Sql("""
                UPDATE sqf.sequfloh_tracking_records
                SET "Status" = 3, "PauseReason" = COALESCE("PauseReason", "HoldReason"), "HoldReason" = NULL
                WHERE "Status" = 4;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequfloh_test_files",
                schema: "sqf");

            migrationBuilder.DropColumn(
                name: "StatusNote",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "StatusTag",
                schema: "sqf",
                table: "sequfloh_work_instructions");

            migrationBuilder.DropColumn(
                name: "PartsReplaced",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QuantityCompleted",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QuantityNotRepaired",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "QuantityTarget",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "StatusNote",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "StatusTag",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "TestColumns",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "StatusNote",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "StatusTag",
                schema: "sqf",
                table: "parts");
        }
    }
}
