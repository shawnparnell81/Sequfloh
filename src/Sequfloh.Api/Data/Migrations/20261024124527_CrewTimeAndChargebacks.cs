using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class CrewTimeAndChargebacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sequfloh_chargeback_reports",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SupplierReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PartDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TaskDescription = table.Column<string>(type: "text", nullable: true),
                    Result = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    QtyInspected = table.Column<int>(type: "integer", nullable: true),
                    QtyBad = table.Column<int>(type: "integer", nullable: true),
                    TotalPersonnel = table.Column<int>(type: "integer", nullable: true),
                    TotalHours = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    CrewHours = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    UserHours = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    DateFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    DateTo = table.Column<DateOnly>(type: "date", nullable: true),
                    HourlyRate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    DiscoveryMethod = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SupplierPartNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LotDateCodes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RootCause = table.Column<string>(type: "text", nullable: true),
                    CorrectiveAction = table.Column<string>(type: "text", nullable: true),
                    Parts = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    CostLines = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_chargeback_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_chargeback_reports_sequfloh_tracking_records_Track~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_crew_sessions",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CrewSize = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PausedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PausedSeconds = table.Column<long>(type: "bigint", nullable: false),
                    Events = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Entered = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_crew_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_crew_sessions_sequfloh_tracking_records_TrackingRe~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_chargeback_reports_CreatedAt",
                schema: "sqf",
                table: "sequfloh_chargeback_reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_chargeback_reports_TrackingRecordId",
                schema: "sqf",
                table: "sequfloh_chargeback_reports",
                column: "TrackingRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_crew_sessions_TrackingRecordId_StartedAt",
                schema: "sqf",
                table: "sequfloh_crew_sessions",
                columns: new[] { "TrackingRecordId", "StartedAt" });

            // the built-in roles get crew time and chargeback reports
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", p FROM sqf.roles, unnest(ARRAY['sequfloh.manage_crew_time', 'sequfloh.manage_chargebacks']) AS p WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequfloh_chargeback_reports",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_crew_sessions",
                schema: "sqf");
        }
    }
}
