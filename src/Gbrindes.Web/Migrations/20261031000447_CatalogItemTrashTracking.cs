using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class CatalogItemTrashTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                table: "CatalogItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_DeletedBy",
                table: "CatalogItems",
                column: "DeletedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_CatalogItems_AspNetUsers_DeletedBy",
                table: "CatalogItems",
                column: "DeletedBy",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CatalogItems_AspNetUsers_DeletedBy",
                table: "CatalogItems");

            migrationBuilder.DropIndex(
                name: "IX_CatalogItems_DeletedBy",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "CatalogItems");
        }
    }
}
