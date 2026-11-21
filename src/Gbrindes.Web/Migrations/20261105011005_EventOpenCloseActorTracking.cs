using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class EventOpenCloseActorTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClosedBy",
                table: "Events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenedBy",
                table: "Events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_ClosedBy",
                table: "Events",
                column: "ClosedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Events_OpenedBy",
                table: "Events",
                column: "OpenedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_AspNetUsers_ClosedBy",
                table: "Events",
                column: "ClosedBy",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Events_AspNetUsers_OpenedBy",
                table: "Events",
                column: "OpenedBy",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_AspNetUsers_ClosedBy",
                table: "Events");

            migrationBuilder.DropForeignKey(
                name: "FK_Events_AspNetUsers_OpenedBy",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_ClosedBy",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_OpenedBy",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ClosedBy",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "OpenedBy",
                table: "Events");
        }
    }
}
