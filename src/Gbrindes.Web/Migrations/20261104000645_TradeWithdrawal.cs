using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class TradeWithdrawal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndustryId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveredById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedByName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    ReceivedByEmail = table.Column<string>(type: "character varying(190)", maxLength: 190, nullable: true),
                    ReceivedByDocument = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ReceivedByPhone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SignaturePath = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IdempotencyKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deliveries_AspNetUsers_DeliveredById",
                        column: x => x.DeliveredById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Deliveries_Industries_IndustryId",
                        column: x => x.IndustryId,
                        principalTable: "Industries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Deliveries_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    BalanceAfter = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryItems", x => x.Id);
                    table.CheckConstraint("CK_DeliveryItems_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_DeliveryItems_CatalogItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeliveryItems_Deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "Deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_DeliveryId",
                table: "StockMovements",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Code",
                table: "Deliveries",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_DeliveredById",
                table: "Deliveries",
                column: "DeliveredById");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_IdempotencyKeyHash",
                table: "Deliveries",
                column: "IdempotencyKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_IndustryId",
                table: "Deliveries",
                column: "IndustryId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_RequestId_CreatedAt",
                table: "Deliveries",
                columns: new[] { "RequestId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryItems_DeliveryId_ItemId",
                table: "DeliveryItems",
                columns: new[] { "DeliveryId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryItems_ItemId",
                table: "DeliveryItems",
                column: "ItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Deliveries_DeliveryId",
                table: "StockMovements",
                column: "DeliveryId",
                principalTable: "Deliveries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Deliveries_DeliveryId",
                table: "StockMovements");

            migrationBuilder.DropTable(
                name: "DeliveryItems");

            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_DeliveryId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "DeliveryId",
                table: "StockMovements");
        }
    }
}
