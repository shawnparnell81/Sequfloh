using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportLayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sequfloh_home_layouts",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sections = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_home_layouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_home_layouts_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_import_layouts",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Layout = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_import_layouts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_home_layouts_UserId",
                schema: "sqf",
                table: "sequfloh_home_layouts",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_import_layouts_Name",
                schema: "sqf",
                table: "sequfloh_import_layouts",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequfloh_home_layouts",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_import_layouts",
                schema: "sqf");
        }
    }
}
