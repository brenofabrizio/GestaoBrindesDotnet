using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class TradeInvoiceAttachment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoicePath",
                table: "Requests",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoicePath",
                table: "Requests");
        }
    }
}
