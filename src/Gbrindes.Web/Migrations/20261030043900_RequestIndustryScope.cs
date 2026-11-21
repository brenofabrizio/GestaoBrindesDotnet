using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class RequestIndustryScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IndustryId",
                table: "Requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_IndustryId",
                table: "Requests",
                column: "IndustryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Requests_Industries_IndustryId",
                table: "Requests",
                column: "IndustryId",
                principalTable: "Industries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Requests_Industries_IndustryId",
                table: "Requests");

            migrationBuilder.DropIndex(
                name: "IX_Requests_IndustryId",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "IndustryId",
                table: "Requests");
        }
    }
}
