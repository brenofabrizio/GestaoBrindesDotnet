using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class TradeReceiving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InternalRequestItems_Quantities",
                table: "RequestItems");

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNo",
                table: "Requests",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuantityReceived",
                table: "RequestItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_InternalRequestItems_Quantities",
                table: "RequestItems",
                sql: "\"QuantityRequested\" > 0 AND \"QuantityReserved\" >= 0 AND \"QuantityReceived\" >= 0 AND \"QuantityDelivered\" >= 0 AND (\"QuantityApproved\" IS NULL OR \"QuantityApproved\" >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InternalRequestItems_Quantities",
                table: "RequestItems");

            migrationBuilder.DropColumn(
                name: "InvoiceNo",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "QuantityReceived",
                table: "RequestItems");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InternalRequestItems_Quantities",
                table: "RequestItems",
                sql: "\"QuantityRequested\" > 0 AND \"QuantityReserved\" >= 0 AND \"QuantityDelivered\" >= 0 AND (\"QuantityApproved\" IS NULL OR \"QuantityApproved\" >= 0)");
        }
    }
}
