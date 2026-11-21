using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class StockMovementHistoryMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PurchaseTicketNo",
                table: "StockMovements",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "StockMovements",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recipient",
                table: "StockMovements",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PurchaseTicketNo",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "Recipient",
                table: "StockMovements");
        }
    }
}
