using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlankForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BlankFormId",
                schema: "sqf",
                table: "imported_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BlankFormVersion",
                schema: "sqf",
                table: "imported_files",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBlankForm",
                schema: "sqf",
                table: "imported_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_imported_files_BlankFormId",
                schema: "sqf",
                table: "imported_files",
                column: "BlankFormId");

            migrationBuilder.AddForeignKey(
                name: "FK_imported_files_imported_files_BlankFormId",
                schema: "sqf",
                table: "imported_files",
                column: "BlankFormId",
                principalSchema: "sqf",
                principalTable: "imported_files",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_imported_files_imported_files_BlankFormId",
                schema: "sqf",
                table: "imported_files");

            migrationBuilder.DropIndex(
                name: "IX_imported_files_BlankFormId",
                schema: "sqf",
                table: "imported_files");

            migrationBuilder.DropColumn(
                name: "BlankFormId",
                schema: "sqf",
                table: "imported_files");

            migrationBuilder.DropColumn(
                name: "BlankFormVersion",
                schema: "sqf",
                table: "imported_files");

            migrationBuilder.DropColumn(
                name: "IsBlankForm",
                schema: "sqf",
                table: "imported_files");
        }
    }
}
