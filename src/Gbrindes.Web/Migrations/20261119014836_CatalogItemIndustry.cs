using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class CatalogItemIndustry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IndustryId",
                table: "CatalogItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_IndustryId",
                table: "CatalogItems",
                column: "IndustryId");

            migrationBuilder.AddForeignKey(
                name: "FK_CatalogItems_Industries_IndustryId",
                table: "CatalogItems",
                column: "IndustryId",
                principalTable: "Industries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CatalogItems_Industries_IndustryId",
                table: "CatalogItems");

            migrationBuilder.DropIndex(
                name: "IX_CatalogItems_IndustryId",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "IndustryId",
                table: "CatalogItems");
        }
    }
}
