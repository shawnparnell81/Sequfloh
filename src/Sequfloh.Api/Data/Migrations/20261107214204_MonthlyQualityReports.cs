using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class MonthlyQualityReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monthly_quality_reports",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FlatRate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Body = table.Column<string>(type: "jsonb", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalizedById = table.Column<Guid>(type: "uuid", nullable: true),
                    FinalizedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReopenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReopenedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReopenReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monthly_quality_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_monthly_quality_reports_users_FinalizedById",
                        column: x => x.FinalizedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monthly_quality_reports_users_ReopenedById",
                        column: x => x.ReopenedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monthly_report_files",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    FileType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UploadedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReportingCutoff = table.Column<DateOnly>(type: "date", nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monthly_report_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_monthly_report_files_monthly_quality_reports_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "sqf",
                        principalTable: "monthly_quality_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monthly_quality_reports_FinalizedById",
                schema: "sqf",
                table: "monthly_quality_reports",
                column: "FinalizedById");

            migrationBuilder.CreateIndex(
                name: "IX_monthly_quality_reports_ReopenedById",
                schema: "sqf",
                table: "monthly_quality_reports",
                column: "ReopenedById");

            migrationBuilder.CreateIndex(
                name: "IX_monthly_quality_reports_Year_Month",
                schema: "sqf",
                table: "monthly_quality_reports",
                columns: new[] { "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monthly_report_files_ReportId_FileType_CreatedAt",
                schema: "sqf",
                table: "monthly_report_files",
                columns: new[] { "ReportId", "FileType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monthly_report_files_Year_Month_FileType",
                schema: "sqf",
                table: "monthly_report_files",
                columns: new[] { "Year", "Month", "FileType" });

            // The built-in Administrator role holds every permission, including monthly quality reports.
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", p FROM sqf.roles
                CROSS JOIN (VALUES
                    ('sequfloh.monthly_reports_view'),
                    ('sequfloh.monthly_reports_edit'),
                    ('sequfloh.monthly_reports_finalize')) AS perms(p)
                WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM sqf.role_permissions
                WHERE "Permission" IN ('sequfloh.monthly_reports_view', 'sequfloh.monthly_reports_edit', 'sequfloh.monthly_reports_finalize');
                """);

            migrationBuilder.DropTable(
                name: "monthly_report_files",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "monthly_quality_reports",
                schema: "sqf");
        }
    }
}
