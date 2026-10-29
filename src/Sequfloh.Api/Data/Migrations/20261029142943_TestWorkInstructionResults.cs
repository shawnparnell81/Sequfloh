using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class TestWorkInstructionResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TestTemplate",
                schema: "sqf",
                table: "sequfloh_reference_values",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sequfloh_test_part_results",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PartIndex = table.Column<int>(type: "integer", nullable: false),
                    TemplateKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Values = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EditedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RemoveReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_test_part_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_test_part_results_sequfloh_tracking_records_Tracki~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_test_specs",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TemplateKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Values = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_test_specs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_test_specs_sequfloh_tracking_records_TrackingRecor~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_test_part_results_TrackingRecordId_PartIndex",
                schema: "sqf",
                table: "sequfloh_test_part_results",
                columns: new[] { "TrackingRecordId", "PartIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_test_specs_TrackingRecordId",
                schema: "sqf",
                table: "sequfloh_test_specs",
                column: "TrackingRecordId",
                unique: true);
            // the seeded Fuel Pump product family record gets the fuel pump test template; every other family has none until an administrator sets one
            migrationBuilder.Sql("UPDATE sqf.sequfloh_reference_values SET \"TestTemplate\" = 'fuel-pump' WHERE \"ListKey\" = 'product-families' AND \"Code\" = 'FUEL_PUMP' AND \"IsSeeded\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequfloh_test_part_results",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_test_specs",
                schema: "sqf");

            migrationBuilder.DropColumn(
                name: "TestTemplate",
                schema: "sqf",
                table: "sequfloh_reference_values");
        }
    }
}
