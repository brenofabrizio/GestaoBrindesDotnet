using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class StockMovementDepartmentReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "StockMovements" AS movement
                SET "DepartmentId" = exit_order."DepartmentId"
                FROM "StockExitOrders" AS exit_order
                WHERE exit_order."MovementId" = movement."Id"
                  AND exit_order."DepartmentId" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_DepartmentId",
                table: "StockMovements",
                column: "DepartmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Departments_DepartmentId",
                table: "StockMovements",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Departments_DepartmentId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_DepartmentId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "StockMovements");
        }
    }
}
