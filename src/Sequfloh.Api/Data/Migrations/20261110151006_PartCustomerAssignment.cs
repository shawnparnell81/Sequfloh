using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartCustomerAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                schema: "sqf",
                table: "parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartNumberKey",
                schema: "sqf",
                table: "parts",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                computedColumnSql: "lower(btrim(\"PartNumber\"))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_parts_CustomerId",
                schema: "sqf",
                table: "parts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_parts_CustomerId_TypeId_PartNumberKey",
                schema: "sqf",
                table: "parts",
                columns: new[] { "CustomerId", "TypeId", "PartNumberKey" });

            migrationBuilder.CreateIndex(
                name: "IX_parts_DrawingNumber",
                schema: "sqf",
                table: "parts",
                column: "DrawingNumber");

            migrationBuilder.AddForeignKey(
                name: "FK_parts_sequfloh_reference_values_CustomerId",
                schema: "sqf",
                table: "parts",
                column: "CustomerId",
                principalSchema: "sqf",
                principalTable: "sequfloh_reference_values",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Seed the customer list only. Existing parts keep a null CustomerId until an administrator confirms an assignment.
            migrationBuilder.Sql("""
                INSERT INTO sqf.sequfloh_reference_values
                    ("Id", "ListKey", "Code", "Label", "SortOrder", "IsActive", "IsDefault", "IsSeeded", "CreatedAt", "UpdatedAt")
                SELECT v.id, 'customers', v.code, v.label, v.sort, true, false, true, now(), now()
                FROM (VALUES
                    ('a1000000-0000-7000-8000-000000000001'::uuid, 'NAPA_US', 'NAPA US', 10),
                    ('a1000000-0000-7000-8000-000000000002'::uuid, 'NAPA_CANADA', 'NAPA Canada', 20),
                    ('a1000000-0000-7000-8000-000000000003'::uuid, 'O_REILLY', 'O''Reilly', 30),
                    ('a1000000-0000-7000-8000-000000000004'::uuid, 'AUTOZONE', 'AutoZone', 40)
                ) AS v(id, code, label, sort)
                WHERE NOT EXISTS (
                    SELECT 1 FROM sqf.sequfloh_reference_values e
                    WHERE e."ListKey" = 'customers' AND e."Code" = v.code)
                AND NOT EXISTS (
                    SELECT 1 FROM sqf.sequfloh_deleted_seed_values d
                    WHERE d."ListKey" = 'customers' AND d."Code" = v.code);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the assignment column and its indexes. Customer list rows are kept, including any an administrator added or renamed.
            migrationBuilder.DropForeignKey(
                name: "FK_parts_sequfloh_reference_values_CustomerId",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "IX_parts_CustomerId",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "IX_parts_CustomerId_TypeId_PartNumberKey",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "IX_parts_DrawingNumber",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "PartNumberKey",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                schema: "sqf",
                table: "parts");
        }
    }
}
