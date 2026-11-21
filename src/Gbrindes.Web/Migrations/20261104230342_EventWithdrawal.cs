using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class EventWithdrawal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "RequestId",
                table: "Deliveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "Deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_EventId_CreatedAt",
                table: "Deliveries",
                columns: new[] { "EventId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_Events_EventId",
                table: "Deliveries",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_Events_EventId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_EventId_CreatedAt",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "Deliveries");

            migrationBuilder.AlterColumn<Guid>(
                name: "RequestId",
                table: "Deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
