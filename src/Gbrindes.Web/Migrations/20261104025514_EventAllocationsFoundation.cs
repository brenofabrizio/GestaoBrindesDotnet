using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class EventAllocationsFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndustryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityAllocated = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    QuantityWithdrawn = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventAllocations", x => x.Id);
                    table.CheckConstraint("CK_EventAllocations_Quantities", "\"QuantityAllocated\" >= 0 AND \"QuantityWithdrawn\" >= 0 AND \"QuantityWithdrawn\" <= \"QuantityAllocated\"");
                    table.ForeignKey(
                        name: "FK_EventAllocations_CatalogItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventAllocations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventAllocations_Industries_IndustryId",
                        column: x => x.IndustryId,
                        principalTable: "Industries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventAllocations_EventId_IndustryId_ItemId",
                table: "EventAllocations",
                columns: new[] { "EventId", "IndustryId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventAllocations_IndustryId",
                table: "EventAllocations",
                column: "IndustryId");

            migrationBuilder.CreateIndex(
                name: "IX_EventAllocations_ItemId",
                table: "EventAllocations",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventAllocations");
        }
    }
}
