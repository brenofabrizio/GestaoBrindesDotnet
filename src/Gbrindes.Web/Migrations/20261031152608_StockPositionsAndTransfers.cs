using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class StockPositionsAndTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FromLocationId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IndustryId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ToLocationId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TransferId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StockPositions",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockPositions", x => new { x.ItemId, x.LocationId });
                    table.CheckConstraint("CK_StockPositions_Quantity", "\"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockPositions_CatalogItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StockPositions_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_FromLocationId",
                table: "StockMovements",
                column: "FromLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_IndustryId",
                table: "StockMovements",
                column: "IndustryId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_LocationId",
                table: "StockMovements",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ToLocationId",
                table: "StockMovements",
                column: "ToLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockPositions_LocationId_ItemId",
                table: "StockPositions",
                columns: new[] { "LocationId", "ItemId" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Industries_IndustryId",
                table: "StockMovements",
                column: "IndustryId",
                principalTable: "Industries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Locations_FromLocationId",
                table: "StockMovements",
                column: "FromLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Locations_LocationId",
                table: "StockMovements",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Locations_ToLocationId",
                table: "StockMovements",
                column: "ToLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                INSERT INTO "StockPositions" ("ItemId", "LocationId", "Quantity", "UpdatedAt")
                SELECT stock."ItemId",
                       COALESCE(
                           (SELECT location."Id" FROM "Locations" location WHERE location."Id" = item."LocationId" AND location."Active" = TRUE AND location."DeletedAt" IS NULL),
                           (SELECT location."Id" FROM "Locations" location WHERE location."Kind" = 'cd' AND location."Active" = TRUE AND location."DeletedAt" IS NULL ORDER BY location."Id" LIMIT 1),
                           (SELECT location."Id" FROM "Locations" location WHERE location."Active" = TRUE AND location."DeletedAt" IS NULL ORDER BY location."Id" LIMIT 1)
                       ),
                       stock."OnHand",
                       CURRENT_TIMESTAMP
                  FROM "Stock" stock
                  JOIN "CatalogItems" item ON item."Id" = stock."ItemId"
                 WHERE stock."OnHand" > 0
                   AND COALESCE(
                       (SELECT location."Id" FROM "Locations" location WHERE location."Id" = item."LocationId" AND location."Active" = TRUE AND location."DeletedAt" IS NULL),
                       (SELECT location."Id" FROM "Locations" location WHERE location."Kind" = 'cd' AND location."Active" = TRUE AND location."DeletedAt" IS NULL ORDER BY location."Id" LIMIT 1),
                       (SELECT location."Id" FROM "Locations" location WHERE location."Active" = TRUE AND location."DeletedAt" IS NULL ORDER BY location."Id" LIMIT 1)
                   ) IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Industries_IndustryId",
                table: "StockMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Locations_FromLocationId",
                table: "StockMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Locations_LocationId",
                table: "StockMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Locations_ToLocationId",
                table: "StockMovements");

            migrationBuilder.DropTable(
                name: "StockPositions");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_FromLocationId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_IndustryId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_LocationId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_ToLocationId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "FromLocationId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "IndustryId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "ToLocationId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "TransferId",
                table: "StockMovements");
        }
    }
}
