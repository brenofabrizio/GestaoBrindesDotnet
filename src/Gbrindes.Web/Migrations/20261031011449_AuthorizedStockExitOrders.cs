using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizedStockExitOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sequences",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Period = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    LastValue = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sequences", x => new { x.Name, x.Period });
                });

            migrationBuilder.CreateTable(
                name: "StockExitOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Recipient = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    IndustryId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseTicketNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DocumentRef = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AuthorizedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MovementId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    IdempotencyRequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockExitOrders", x => x.Id);
                    table.CheckConstraint("CK_StockExitOrders_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_StockExitOrders_AspNetUsers_AuthorizedBy",
                        column: x => x.AuthorizedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockExitOrders_AspNetUsers_ConfirmedBy",
                        column: x => x.ConfirmedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StockExitOrders_AspNetUsers_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StockExitOrders_CatalogItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockExitOrders_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StockExitOrders_Industries_IndustryId",
                        column: x => x.IndustryId,
                        principalTable: "Industries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StockExitOrders_StockMovements_MovementId",
                        column: x => x.MovementId,
                        principalTable: "StockMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_AuthorizedBy_IdempotencyKey",
                table: "StockExitOrders",
                columns: new[] { "AuthorizedBy", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_Code",
                table: "StockExitOrders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_ConfirmedBy",
                table: "StockExitOrders",
                column: "ConfirmedBy");

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_DepartmentId",
                table: "StockExitOrders",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_IndustryId",
                table: "StockExitOrders",
                column: "IndustryId");

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_ItemId",
                table: "StockExitOrders",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_MovementId",
                table: "StockExitOrders",
                column: "MovementId");

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_RequesterId",
                table: "StockExitOrders",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_StockExitOrders_Status_CreatedAt",
                table: "StockExitOrders",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sequences");

            migrationBuilder.DropTable(
                name: "StockExitOrders");
        }
    }
}
