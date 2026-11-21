using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class TradePurchaseTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PurchaseTicketNo",
                table: "Requests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PurchaseTicketNo",
                table: "Requests");
        }
    }
}
