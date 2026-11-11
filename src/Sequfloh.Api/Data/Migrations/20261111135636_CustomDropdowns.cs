using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomDropdowns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_dropdowns",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_dropdowns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "custom_dropdown_placements",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DropdownId = table.Column<Guid>(type: "uuid", nullable: false),
                    Screen = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    ShowInList = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeInCsv = table.Column<bool>(type: "boolean", nullable: false),
                    Filterable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_dropdown_placements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_dropdown_placements_custom_dropdowns_DropdownId",
                        column: x => x.DropdownId,
                        principalSchema: "sqf",
                        principalTable: "custom_dropdowns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "custom_dropdown_values",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DropdownId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_dropdown_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_dropdown_values_custom_dropdowns_DropdownId",
                        column: x => x.DropdownId,
                        principalSchema: "sqf",
                        principalTable: "custom_dropdowns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_values",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DropdownId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_values_custom_dropdown_values_ValueId",
                        column: x => x.ValueId,
                        principalSchema: "sqf",
                        principalTable: "custom_dropdown_values",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_custom_field_values_custom_dropdowns_DropdownId",
                        column: x => x.DropdownId,
                        principalSchema: "sqf",
                        principalTable: "custom_dropdowns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_dropdown_placements_DropdownId_Screen",
                schema: "sqf",
                table: "custom_dropdown_placements",
                columns: new[] { "DropdownId", "Screen" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_dropdown_values_DropdownId_Code",
                schema: "sqf",
                table: "custom_dropdown_values",
                columns: new[] { "DropdownId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_dropdowns_Name",
                schema: "sqf",
                table: "custom_dropdowns",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_DropdownId_RecordType_RecordId",
                schema: "sqf",
                table: "custom_field_values",
                columns: new[] { "DropdownId", "RecordType", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_RecordType_RecordId",
                schema: "sqf",
                table: "custom_field_values",
                columns: new[] { "RecordType", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_ValueId",
                schema: "sqf",
                table: "custom_field_values",
                column: "ValueId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_dropdown_placements",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "custom_field_values",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "custom_dropdown_values",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "custom_dropdowns",
                schema: "sqf");
        }
    }
}
