using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartBrand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BrandId",
                schema: "sqf",
                table: "parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_parts_BrandId",
                schema: "sqf",
                table: "parts",
                column: "BrandId");

            migrationBuilder.AddForeignKey(
                name: "FK_parts_sequfloh_reference_values_BrandId",
                schema: "sqf",
                table: "parts",
                column: "BrandId",
                principalSchema: "sqf",
                principalTable: "sequfloh_reference_values",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Seed the Brands list. Existing parts keep a null BrandId.
            migrationBuilder.Sql("""
                INSERT INTO sqf.sequfloh_reference_values
                    ("Id", "ListKey", "Code", "Label", "SortOrder", "IsActive", "IsDefault", "IsSeeded", "CreatedAt", "UpdatedAt")
                SELECT v.id, 'brands', v.code, v.label, v.sort, true, false, true, now(), now()
                FROM (VALUES
                    ('a2000000-0000-7000-8000-000000000001'::uuid, 'DURALAST', 'Duralast', 10),
                    ('a2000000-0000-7000-8000-000000000002'::uuid, 'MASTERPRO', 'MasterPro', 20),
                    ('a2000000-0000-7000-8000-000000000003'::uuid, 'PROFORMER', 'Proformer', 30),
                    ('a2000000-0000-7000-8000-000000000004'::uuid, 'IMPORT_DIRECT', 'Import Direct', 40),
                    ('a2000000-0000-7000-8000-000000000005'::uuid, 'PRECISION', 'Precision', 50),
                    ('a2000000-0000-7000-8000-000000000006'::uuid, 'DORMAN', 'Dorman', 60),
                    ('a2000000-0000-7000-8000-000000000007'::uuid, 'SPECTRA_PREMIUM', 'Spectra Premium', 70),
                    ('a2000000-0000-7000-8000-000000000008'::uuid, 'MONOSHOCK', 'Monoshock', 80),
                    ('a2000000-0000-7000-8000-000000000009'::uuid, 'UAS', 'UAS', 90),
                    ('a2000000-0000-7000-8000-00000000000a'::uuid, 'WD_FUELMASTER', 'WD Fuelmaster', 100),
                    ('a2000000-0000-7000-8000-00000000000b'::uuid, 'BRAKEMASTER', 'Brakemaster', 110),
                    ('a2000000-0000-7000-8000-00000000000c'::uuid, 'RIDEMASTER', 'RideMaster', 120),
                    ('a2000000-0000-7000-8000-00000000000d'::uuid, 'US_PROFORMER_AIR', 'US Proformer Air', 130),
                    ('a2000000-0000-7000-8000-00000000000e'::uuid, 'NISSAN_AIR', 'Nissan Air', 140),
                    ('a2000000-0000-7000-8000-00000000000f'::uuid, 'NISSAN', 'Nissan', 150),
                    ('a2000000-0000-7000-8000-000000000010'::uuid, 'MASTERPRO_MONOSHOCK', 'MasterPro Monoshock', 160),
                    ('a2000000-0000-7000-8000-000000000011'::uuid, 'CALVERT_RACING', 'Calvert Racing', 170)
                ) AS v(id, code, label, sort)
                WHERE NOT EXISTS (
                    SELECT 1 FROM sqf.sequfloh_reference_values e
                    WHERE e."ListKey" = 'brands' AND e."Code" = v.code)
                AND NOT EXISTS (
                    SELECT 1 FROM sqf.sequfloh_deleted_seed_values d
                    WHERE d."ListKey" = 'brands' AND d."Code" = v.code);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_parts_sequfloh_reference_values_BrandId",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "IX_parts_BrandId",
                schema: "sqf",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "BrandId",
                schema: "sqf",
                table: "parts");
        }
    }
}
